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

                foreach (var deviceItem in selectedDevices)
                {
                    var device = deviceItem.Device;
                    if (device.ID == defaultDevice.ID)
                    {
                        continue;
                    }

                    var wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 50);
                    var buffer = new BufferedWaveProvider(captureFormat)
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(150),
                        DiscardOnBufferOverflow = true
                    };

                    var sampleProvider = buffer.ToSampleProvider();
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

                    var meterProvider = new MeteringSampleProvider(delayProvider, (peak) =>
                    {
                        deviceItem.PeakLevel = peak;
                    });

                    wasapiOut.Init(meterProvider.ToWaveProvider());
                    wasapiOut.Play();
                    outputStreams.Add(wasapiOut);
                    buffers.Add(buffer);
                }

                if (outputStreams.Count == 0)
                {
                    throw new InvalidOperationException("Please select at least one secondary device to mirror your audio to.");
                }

                UpdateRelativeDelays(selectedDevices);

                networkStreamer.Start(captureFormat.SampleRate, 8090);

                loopbackCapture.DataAvailable += (s, args) =>
                {
                    networkStreamer.BroadcastAudio(args.Buffer, args.BytesRecorded);

                    const int TargetBufferDurationMs = 50;
                    int targetBytes = (int)((TargetBufferDurationMs * captureFormat.AverageBytesPerSecond) / 1000.0);
                    targetBytes -= targetBytes % captureFormat.BlockAlign;

                    int toleranceBytes = (int)((20 * captureFormat.AverageBytesPerSecond) / 1000.0);
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
                throw new InvalidOperationException("Error starting audio routing: " + ex.Message, ex);
            }
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
                item.PeakLevel = 0f;
            }

            isConnected = false;
        }

        public void UpdateRelativeDelays(List<DeviceItem> activeDevices)
        {
            var selectedActiveItems = activeDevices.Where(i => i.IsSelected && i.DelayProvider != null).ToList();
            if (selectedActiveItems.Count == 0) return;

            int minDelaySetting = selectedActiveItems.Min(i => i.Delay);
            foreach (var item in selectedActiveItems)
                if (item.DelayProvider != null)
                {
                    item.DelayProvider.DelayMilliseconds = item.Delay - minDelaySetting;
                }
        }
    }
}
