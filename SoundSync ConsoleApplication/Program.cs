using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VirtualAudioMixer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Virtual Audio Mixer (Phase 1) ===");

            // 1. DEVICE ENUMERATION
            var enumerator = new MMDeviceEnumerator();
            var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

            Console.WriteLine("\nAvailable Audio Output Devices:");
            for (int i = 0; i < renderDevices.Count; i++)
            {
                Console.WriteLine($"[{i + 1}] {renderDevices[i].FriendlyName}");
            }

            // 2. USER SELECTION
            Console.Write("\nEnter the numbers of the devices you want to broadcast to (e.g. 1,2): ");
            string input = Console.ReadLine();

            var selectedIndices = input?.Split(',')
                .Select(s => s.Trim())
                .Where(s => int.TryParse(s, out _))
                .Select(int.Parse)
                .Where(i => i >= 1 && i <= renderDevices.Count)
                .Select(i => i - 1)
                .ToList();

            if (selectedIndices == null || selectedIndices.Count == 0)
            {
                Console.WriteLine("No valid devices selected. Press any key to exit.");
                Console.ReadKey();
                return;
            }
            List<MMDevice> selectedDevices = selectedIndices.Select(i => renderDevices[i]).ToList();

            // Find out what the current default Windows playback device is
            var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            // 3. SYSTEM AUDIO CAPTURE (LOOPBACK)
            using var loopbackCapture = new WasapiLoopbackCapture();
            var captureFormat = loopbackCapture.WaveFormat;
            Console.WriteLine($"\nCapturing Default Audio Engine Format: {captureFormat.SampleRate}Hz, {captureFormat.Channels} channels, {captureFormat.BitsPerSample} bits");

            // 4. MULTI-ENDPOINT AUDIO ROUTING
            var outputStreams = new List<WasapiOut>();
            var buffers = new List<BufferedWaveProvider>();

            try
            {
                foreach (var device in selectedDevices)
                {
                    // FIX #1: Prevent the feedback loop!
                    if (device.ID == defaultDevice.ID)
                    {
                        Console.WriteLine($"-> SKIPPING: {device.FriendlyName}");
                        Console.WriteLine("   (This is your default device. Windows is already playing audio here natively. Routing to it again causes an echo!)");
                        continue;
                    }

                    // Create an output stream targeting a low latency of 50ms
                    var wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 50);

                    // FIX #2: Keep the bucket extremely small (150ms max)
                    var buffer = new BufferedWaveProvider(captureFormat)
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(150),
                        DiscardOnBufferOverflow = true
                    };

                    wasapiOut.Init(buffer);
                    wasapiOut.Play();
                    outputStreams.Add(wasapiOut);
                    buffers.Add(buffer);
                    Console.WriteLine($"-> Successfully routed to: {device.FriendlyName}");
                }

                // If all selected devices were skipped (e.g. they only chose the default device)
                if (outputStreams.Count == 0)
                {
                    Console.WriteLine("\nNo valid secondary devices were routed. Press any key to exit.");
                    Console.ReadKey();
                    return;
                }

                // This event fires every few milliseconds when Windows hands us raw audio bytes
                loopbackCapture.DataAvailable += (sender, args) =>
                {
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

                // Start capturing
                loopbackCapture.StartRecording();
                Console.WriteLine("\n[ LIVE ] Audio routing is active!");
                Console.WriteLine("Press 'Q' to quit.");

                // Keep the app alive
                while (true)
                {
                    if (Console.KeyAvailable)
                    {
                        var keyInfo = Console.ReadKey(intercept: true);
                        if (keyInfo.Key == ConsoleKey.Q)
                        {
                            break;
                        }
                    }
                    Thread.Sleep(100);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[ERROR] An error occurred: {ex.Message}");
                Console.WriteLine("Hint: If it says 'Unsupported Wave Format', your selected output device does not support the exact Sample Rate of your default playback device.");
            }
            finally
            {
                // 5. CLEANUP
                Console.WriteLine("\nCleaning up audio resources...");

                loopbackCapture.StopRecording();

                foreach (var outputStream in outputStreams)
                {
                    outputStream.Stop();
                    outputStream.Dispose();
                }

                Console.WriteLine("Donqe. Press any key to exit.");
                Console.ReadKey();
            }
        }

    }
}