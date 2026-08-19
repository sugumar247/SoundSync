using SoundSync.Models;
using SoundSync.Services.Providers;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.Linq;

using MeteringSampleProvider = SoundSync.Services.Providers.MeteringSampleProvider;

namespace SoundSync.Services
{
    public class AudioEngine : IAudioEngine
    {
        private MMDeviceEnumerator? enumerator;
        private WasapiLoopbackCapture? loopbackCapture;
        private readonly List<WasapiOut> outputStreams = new List<WasapiOut>();
        private readonly List<BufferedWaveProvider> buffers = new List<BufferedWaveProvider>();
        private bool isConnected = false;

        public bool IsConnected => isConnected;

        public List<MMDevice> GetActiveRenderDevices()
        {
            enumerator ??= new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        }

        public void Connect(List<DeviceItem> selectedDevices, INetworkStreamer networkStreamer, Action<string> logCallback, Action onDisconnectedCallback)
        {
            if (isConnected) return;

            try
            {
                enumerator ??= new MMDeviceEnumerator();
                var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                captureSource = defaultDevice;
                loopbackCapture = new WasapiLoopbackCapture();
                var captureFormat = loopbackCapture.WaveFormat;

                var failures = new List<string>();

                // The caller normally passes an already-filtered list, but honour IsSelected
                // here too: an unticked device must never be opened, whoever calls this.
                foreach (var deviceItem in selectedDevices.Where(d => d.IsSelected))
                {
                    var device = deviceItem.Device;
                    if (device.ID == defaultDevice.ID)
                    {
                        continue;
                    }

                    WasapiOut? wasapiOut = null;
                    try
                    {
                    wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 30);
                    var buffer = new BufferedWaveProvider(captureFormat)
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(100),
                        DiscardOnBufferOverflow = true
                    };

                    // Convert to exactly what this endpoint expects before anything else
                    // touches the stream, so WASAPI is never asked to adapt a layout it
                    // cannot handle. See FormatAdapter for why the order matters.
                    WaveFormat outputFormat = GetOutputFormat(device, captureFormat);
                    ISampleProvider sampleProvider = FormatAdapter.Adapt(
                        buffer.ToSampleProvider(), outputFormat);

                    // Meter the material BEFORE this output's volume, equaliser and delay, and
                    // undo the default device's volume while reporting it. The bar then shows
                    // what is actually playing rather than how loud any one knob is set, so all
                    // the bars agree with each other and stop moving when a volume moves.
                    sampleProvider = new MeteringSampleProvider(sampleProvider, peak =>
                    {
                        deviceItem.PeakLevel = Math.Clamp(peak * sourceMeterGain, 0f, 1f);
                    });

                    var volumeProvider = new VolumeSampleProvider(sampleProvider) { Volume = deviceItem.Volume };
                    deviceItem.VolumeProvider = volumeProvider;

                    var equalizerProvider = new EqualizerSampleProvider(volumeProvider)
                    {
                        BassDb = deviceItem.Bass,
                        MidDb = deviceItem.Mid,
                        TrebleDb = deviceItem.Treble
                    };
                    deviceItem.EqualizerProvider = equalizerProvider;

                    var delayProvider = new DelaySampleProvider(equalizerProvider) { DelayMilliseconds = 0 };
                    deviceItem.DelayProvider = delayProvider;

                    wasapiOut.Init(FormatAdapter.AdaptToWaveProvider(delayProvider, outputFormat));
                    wasapiOut.Play();
                    outputStreams.Add(wasapiOut);
                    buffers.Add(buffer);
                    deviceItem.OutputBuffer = buffer;
                    deviceItem.EndpointLatencyMs = 30;
                    }
                    catch (Exception deviceError)
                    {
                        // One unusable output must not take the whole session down: drop
                        // it, remember why, and keep mirroring to the rest.
                        try { wasapiOut?.Dispose(); } catch { }
                        deviceItem.VolumeProvider = null;
                        deviceItem.EqualizerProvider = null;
                        deviceItem.DelayProvider = null;
                        deviceItem.OutputBuffer = null;
                        string reason = AudioErrors.Explain(deviceError, deviceItem.Name, captureFormat, device);
                        failures.Add(reason);
                        logCallback(reason);
                    }
                }

                if (outputStreams.Count == 0)
                {
                    throw new InvalidOperationException(failures.Count > 0
                        ? "No output could be started.\n\n" + string.Join("\n\n", failures)
                        : "Tick at least one device other than the default one. The default device is the source being mirrored, so it cannot also be a destination.");
                }

                if (failures.Count > 0)
                {
                    logCallback($"Mirroring to {outputStreams.Count} device(s); {failures.Count} skipped.");
                }

                UpdateRelativeDelays(selectedDevices);

                // The phone stream is a bonus, not the job. If its port is taken - another
                // copy of SoundSync, or anything else on 8090 - mirroring must still run.
                try
                {
                    networkStreamer.Start(captureFormat.SampleRate, captureFormat.Channels, 8090);
                }
                catch (Exception streamError)
                {
                    logCallback("Audio mirroring is running, but the phone stream could not start: " +
                                streamError.Message +
                                " Another copy of SoundSync, or another program, is using port 8090.");
                }

                bool captureIsFloat = captureFormat.Encoding == WaveFormatEncoding.IeeeFloat
                                      && captureFormat.BitsPerSample == 32;

                // Scratch space for the distribution layer, reused so the capture callback
                // does not allocate on every packet.
                byte[] distribution = Array.Empty<byte>();

                loopbackCapture.DataAvailable += (s, args) =>
                {
                    // ---- layer 2: distribution -------------------------------------
                    //
                    // One clean copy of the source, with the default device's volume divided
                    // back out, that every consumer draws from - the local outputs and the
                    // network listeners alike. Loopback capture is post-volume, so without
                    // this step everything downstream inherits whatever the master happened
                    // to be set to, and a listener on a phone could never reach full level.
                    //
                    // Per-consumer volume, equaliser and delay come after this, in layer 3,
                    // so no consumer's settings can leak into another's.
                    byte[] source = args.Buffer;
                    int sourceBytes = args.BytesRecorded;

                    float gain = distributionGain;
                    if (captureIsFloat && Math.Abs(gain - 1.0f) > 0.001f)
                    {
                        if (distribution.Length < sourceBytes) distribution = new byte[sourceBytes];
                        for (int i = 0; i + 3 < sourceBytes; i += 4)
                        {
                            float v = BitConverter.ToSingle(args.Buffer, i) * gain;
                            BitConverter.TryWriteBytes(distribution.AsSpan(i, 4), Math.Clamp(v, -1f, 1f));
                        }
                        source = distribution;
                    }

                    networkStreamer.BroadcastAudio(source, sourceBytes);

                    // Level of the source itself, before this app or any output touches it.
                    if (captureIsFloat)
                    {
                        float peak = 0f;
                        for (int i = 0; i + 3 < sourceBytes; i += 4)
                        {
                            float v = Math.Abs(BitConverter.ToSingle(source, i));
                            if (v > peak) peak = v;
                        }
                        SourcePeakLevel = Math.Clamp(peak * sourceMeterGain, 0f, 1f);
                    }

                    const int TargetBufferDurationMs = 30;
                    int targetBytes = (int)((TargetBufferDurationMs * captureFormat.AverageBytesPerSecond) / 1000.0);
                    targetBytes -= targetBytes % captureFormat.BlockAlign;

                    int toleranceBytes = (int)((10 * captureFormat.AverageBytesPerSecond) / 1000.0);
                    toleranceBytes -= toleranceBytes % captureFormat.BlockAlign;

                    foreach (var buffer in buffers)
                    {
                        int currentBytes = buffer.BufferedBytes;
                        if (currentBytes > targetBytes + toleranceBytes)
                        {
                            int bytesToDiscard = currentBytes - targetBytes;
                            bytesToDiscard -= bytesToDiscard % captureFormat.BlockAlign;
                            if (bytesToDiscard > 0)
                            {
                                byte[] temp = new byte[bytesToDiscard];
                                buffer.Read(temp, 0, bytesToDiscard);
                            }
                        }
                        else if (currentBytes < targetBytes - toleranceBytes)
                        {
                            int bytesToPad = targetBytes - currentBytes;
                            bytesToPad -= bytesToPad % captureFormat.BlockAlign;
                            if (bytesToPad > 0)
                            {
                                byte[] silence = new byte[bytesToPad];
                                buffer.AddSamples(silence, 0, bytesToPad);
                            }
                        }
                        buffer.AddSamples(source, 0, sourceBytes);
                    }
                };
                // Windows kills the capture when the source endpoint is reconfigured or
                // unplugged. Without this the app sits there believing it is still mirroring
                // while every output has gone quiet.
                loopbackCapture.RecordingStopped += (s, e) =>
                {
                    if (!isConnected) return;
                    string why = e.Exception != null
                        ? AudioErrors.Describe(e.Exception, "capture device")
                        : "The source device stopped providing audio.";
                    logCallback("Mirroring stopped: " + why);
                    onDisconnectedCallback();
                };

                loopbackCapture.StartRecording();
                isConnected = true;
            }
            catch (Exception ex)
            {
                Disconnect(selectedDevices);
                onDisconnectedCallback();
                throw new InvalidOperationException(
                    ex is InvalidOperationException ? ex.Message
                    : "Could not start mirroring.\n\n" + AudioErrors.Describe(ex, "audio engine"), ex);
            }
        }

        /// <summary>Format this endpoint expects, falling back to the captured one.</summary>
        private static WaveFormat GetOutputFormat(MMDevice device, WaveFormat fallback)
        {
            try { return device.AudioClient.MixFormat; }
            catch { return fallback; }
        }

        public void Disconnect(List<DeviceItem> activeDevices)
        {
            if (loopbackCapture != null)
            {
                try { loopbackCapture.StopRecording(); } catch { }
                try { loopbackCapture.Dispose(); } catch { }
                loopbackCapture = null;
            }

            foreach (var stream in outputStreams)
            {
                try { stream.Stop(); } catch { }
                try { stream.Dispose(); } catch { }
            }
            outputStreams.Clear();
            buffers.Clear();

            foreach (var item in activeDevices)
            {
                item.VolumeProvider = null;
                item.EqualizerProvider = null;
                item.DelayProvider = null;
                item.OutputBuffer = null;
                item.PeakLevel = 0f;
            }

            SourcePeakLevel = 0f;
            captureSource = null;
            isConnected = false;
        }

        /// <summary>
        /// Highest make-up gain allowed when undoing the default device's volume. At 8x the
        /// signal is already lifted by 18 dB, and going further would mostly raise the noise
        /// floor of a stream that had almost nothing left in it.
        /// </summary>
        private const float MaxMakeUpGain = 8.0f;

        /// <summary>
        /// Scale the signal meters use so they read the source material rather than whatever
        /// the default device's volume happens to be. Always applied, even when the make-up
        /// gain itself is switched off - a meter that follows a volume knob tells you nothing
        /// about whether audio is arriving.
        /// </summary>
        private volatile float sourceMeterGain = 1.0f;

        /// <summary>
        /// Gain applied once, at the distribution layer, to undo the default device's volume.
        /// Every consumer draws from the result, so no one inherits the master's setting.
        /// </summary>
        private volatile float distributionGain = 1.0f;

        /// <summary>The device being captured, so its level in decibels can be read.</summary>
        private MMDevice? captureSource;

        /// <summary>Peak of the captured source, corrected for the default device's volume.</summary>
        public float SourcePeakLevel { get; private set; }

        /// <summary>Tells the engine what the default device's volume is right now.</summary>
        public void SetDefaultDeviceVolume(float volume)
        {
            // Only meaningful while the correction is off; ApplyMakeUpGain sets both.
            if (Math.Abs(distributionGain - 1.0f) < 0.001f) sourceMeterGain = MakeUpGainForDevice(captureSource);
        }

        /// <summary>
        /// Cancels the default device's volume out of the mirrored signal.
        ///
        /// WASAPI loopback hands over the audio AFTER Windows has applied the default
        /// device's volume, so a master sitting at 20% means the mirrors only ever receive a
        /// fifth of the signal - and then attenuate it again with their own volume. Dividing
        /// it back out gives every mirror the full-strength audio, so its own Windows volume
        /// is the only thing setting how loud it plays.
        /// </summary>
        public static float MakeUpGainFor(float defaultDeviceVolume)
        {
            if (defaultDeviceVolume <= 0.001f) return 1.0f;   // nothing to recover from silence
            return Math.Clamp(1.0f / defaultDeviceVolume, 1.0f, MaxMakeUpGain);
        }

        /// <summary>
        /// Gain that undoes an endpoint's volume, worked out from the decibel value Windows
        /// reports rather than from the 0..1 scalar.
        ///
        /// The scalar is a perceptual position on the slider, not an amplitude: at 40% Windows
        /// is applying -13.9 dB, which is a factor of 0.202, not 0.4. Measured against a known
        /// tone on this machine, amplitude = 10^(dB/20) predicts the captured level exactly at
        /// every point, while treating the scalar as a factor under-compensates by half.
        /// </summary>
        public static float MakeUpGainForDecibels(float decibels)
        {
            if (float.IsNaN(decibels) || float.IsInfinity(decibels)) return 1.0f;

            double factor = Math.Pow(10.0, decibels / 20.0);
            if (factor <= 0.0001) return 1.0f;                // effectively silent, nothing to lift

            return (float)Math.Clamp(1.0 / factor, 1.0, MaxMakeUpGain);
        }

        /// <summary>Reads the endpoint's level in decibels and returns the gain that undoes it.</summary>
        public static float MakeUpGainForDevice(MMDevice? device)
        {
            if (device == null) return 1.0f;
            try
            {
                if (device.AudioEndpointVolume.Mute) return 1.0f;
                return MakeUpGainForDecibels(device.AudioEndpointVolume.MasterVolumeLevel);
            }
            catch { return 1.0f; }
        }

        /// <summary>Applies the make-up gain to every live output.</summary>
        public void ApplyMakeUpGain(List<DeviceItem> activeDevices, float defaultDeviceVolume, bool enabled)
        {
            // Read the real level in decibels from the endpoint. The 0..1 scalar passed in is
            // a slider position, not an amplitude, and using it under-compensates by half.
            float full = MakeUpGainForDevice(captureSource);

            // Layer 2 carries the correction, so local outputs and network listeners get the
            // same clean signal. Layer 3 is left at unity for each output's own settings.
            distributionGain = enabled ? full : 1.0f;

            // Meters always read as if the correction were on, even when it is not: a bar
            // that tracks a volume knob says nothing about whether audio is arriving.
            sourceMeterGain = enabled ? 1.0f : full;

            foreach (var item in activeDevices)
                if (item.VolumeProvider != null)
                    item.VolumeProvider.Volume = 1.0f;
        }

        public void UpdateRelativeDelays(List<DeviceItem> activeDevices)
        {
            // Each output holds back by its own setting. Subtracting the smallest setting
            // across outputs, as this used to, meant a single mirrored output always came
            // out at zero however far the slider moved - the earliest device is always
            // itself. Delay is additive only: the default device plays straight from
            // Windows and cannot be pulled earlier.
            foreach (var item in activeDevices)
            {
                if (item.DelayProvider == null) continue;
                item.DelayProvider.DelayMilliseconds = Math.Max(0, item.Delay);
            }
        }
    }
}
