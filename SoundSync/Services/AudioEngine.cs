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

                loopbackCapture.DataAvailable += (s, args) =>
                {
                    networkStreamer.BroadcastAudio(args.Buffer, args.BytesRecorded);

                    // Level of the source itself, before this app or any output touches it.
                    if (captureIsFloat)
                    {
                        float peak = 0f;
                        for (int i = 0; i + 3 < args.BytesRecorded; i += 4)
                        {
                            float v = Math.Abs(BitConverter.ToSingle(args.Buffer, i));
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
                        buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
                    }
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

        /// <summary>Peak of the captured source, corrected for the default device's volume.</summary>
        public float SourcePeakLevel { get; private set; }

        /// <summary>Tells the engine what the default device's volume is right now.</summary>
        public void SetDefaultDeviceVolume(float volume) => sourceMeterGain = MakeUpGainFor(volume);

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

        /// <summary>Applies the make-up gain to every live output.</summary>
        public void ApplyMakeUpGain(List<DeviceItem> activeDevices, float defaultDeviceVolume, bool enabled)
        {
            float gain = enabled ? MakeUpGainFor(defaultDeviceVolume) : 1.0f;
            foreach (var item in activeDevices)
                if (item.VolumeProvider != null)
                    item.VolumeProvider.Volume = gain;
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
