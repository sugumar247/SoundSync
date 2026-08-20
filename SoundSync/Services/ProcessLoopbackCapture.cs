using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace SoundSync.Services
{
    /// <summary>
    /// Captures everything the machine is playing, taken before the output device's volume
    /// is applied.
    ///
    /// Ordinary loopback capture taps an endpoint, and Windows has already applied that
    /// endpoint's volume by then - so mirroring from a PC turned down to 20% delivers a
    /// fifth of the signal, and mirroring from a muted one delivers nothing. Everything
    /// downstream then has to guess its way back, which cannot be done once the signal is
    /// gone.
    ///
    /// Process loopback capture, added in Windows 10 build 19041, taps the render streams
    /// themselves. Measured on this machine against a fixed tone: the level arrives
    /// unchanged at master 100%, 50%, 10% and while muted, where endpoint loopback fell
    /// from -33.9 dBFS to -68.9 dBFS and then to silence. The per-app slider in the Volume
    /// Mixer still applies, which is what a person means by turning one app down.
    ///
    /// It is asked to exclude this process's own tree, so one stream carries every other
    /// program on the machine, across every output device, and the mirrored copies this app
    /// is itself rendering are never captured back.
    /// </summary>
    public sealed class ProcessLoopbackCapture : IWaveIn
    {
        /// <summary>Documented as 20348; Windows itself accepts it from 19041.</summary>
        public const int MinimumWindowsBuild = 19041;

        private const string VirtualDevice = @"VAD\Process_Loopback";

        private const int ShareModeShared = 0;
        private const uint StreamFlagsLoopback = 0x00020000;
        private const uint StreamFlagsEventCallback = 0x00040000;

        private const int ActivationTypeProcessLoopback = 1;
        private const int ModeExcludeTargetProcessTree = 1;

        private const int BufferFlagsSilent = 0x2;

        private static Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        private static Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

        [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int ActivateAudioInterfaceAsync(
            string deviceInterfacePath, ref Guid riid, IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation operation);

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceCompletionHandler
        {
            void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
        }

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceAsyncOperation
        {
            [PreserveSig] int GetActivateResult(out int activateResult,
                [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration,
                long periodicity, IntPtr format, IntPtr audioSessionGuid);
            [PreserveSig] int GetBufferSize(out uint numBufferFrames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr eventHandle);
            [PreserveSig] int GetService(ref Guid interfaceId,
                [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint numFrames, out uint flags,
                out long devicePosition, out long qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint numFramesRead);
            [PreserveSig] int GetNextPacketSize(out uint numFrames);
        }

        private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
        {
            public readonly ManualResetEventSlim Done = new(false);
            public int ActivateResult;
            public object? Activated;

            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
            {
                try { operation.GetActivateResult(out ActivateResult, out Activated); }
                catch { ActivateResult = -1; }
                finally { Done.Set(); }
            }
        }

        private IAudioClient? client;
        private IAudioCaptureClient? capture;
        private AutoResetEvent? sampleReady;
        private Thread? worker;
        private volatile bool running;

        public WaveFormat WaveFormat { get; set; }

        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;

        /// <summary>Whether this machine is new enough for pre-volume capture.</summary>
        public static bool IsSupported => Environment.OSVersion.Version.Build >= MinimumWindowsBuild;

        public ProcessLoopbackCapture(int sampleRate = 48000, int channels = 2)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public void StartRecording()
        {
            if (running) return;
            if (!IsSupported)
                throw new InvalidOperationException(
                    $"Pre-volume capture needs Windows 10 build {MinimumWindowsBuild} or newer; " +
                    $"this machine reports build {Environment.OSVersion.Version.Build}.");

            IntPtr activationBlob = IntPtr.Zero;
            IntPtr propVariant = IntPtr.Zero;
            IntPtr format = IntPtr.Zero;

            try
            {
                // AUDIOCLIENT_ACTIVATION_PARAMS: type, then the process loopback params.
                activationBlob = Marshal.AllocHGlobal(12);
                Marshal.WriteInt32(activationBlob, 0, ActivationTypeProcessLoopback);
                Marshal.WriteInt32(activationBlob, 4, Environment.ProcessId);
                Marshal.WriteInt32(activationBlob, 8, ModeExcludeTargetProcessTree);

                // Wrapped in a PROPVARIANT of type VT_BLOB. On x64 the size sits at offset 8
                // and the pointer at 16, after the variant's type and padding.
                propVariant = Marshal.AllocHGlobal(32);
                for (int i = 0; i < 32; i++) Marshal.WriteByte(propVariant, i, 0);
                Marshal.WriteInt16(propVariant, 0, 0x0041);
                Marshal.WriteInt32(propVariant, 8, 12);
                Marshal.WriteIntPtr(propVariant, 16, activationBlob);

                var handler = new ActivationHandler();
                int hr = ActivateAudioInterfaceAsync(VirtualDevice, ref IID_IAudioClient,
                                                     propVariant, handler, out var operation);
                ThrowIfFailed(hr, "activating the process loopback device");

                if (!handler.Done.Wait(5000))
                    throw new TimeoutException("Windows did not answer the process loopback activation.");
                ThrowIfFailed(handler.ActivateResult, "the process loopback activation result");

                // Released straight away: holding it makes later activations fail.
                if (operation != null) Marshal.ReleaseComObject(operation);

                client = (IAudioClient)handler.Activated!;

                // This client cannot be asked what it supports - GetMixFormat and
                // IsFormatSupported both return E_NOTIMPL - so the format is simply stated
                // and the virtual device converts to it.
                format = Marshal.AllocHGlobal(18);
                short blockAlign = (short)(WaveFormat.Channels * 4);
                Marshal.WriteInt16(format, 0, 3);                       // IEEE float
                Marshal.WriteInt16(format, 2, (short)WaveFormat.Channels);
                Marshal.WriteInt32(format, 4, WaveFormat.SampleRate);
                Marshal.WriteInt32(format, 8, WaveFormat.SampleRate * blockAlign);
                Marshal.WriteInt16(format, 12, blockAlign);
                Marshal.WriteInt16(format, 14, 32);
                Marshal.WriteInt16(format, 16, 0);

                // The loopback flag is mandatory here: without it Initialize fails with
                // AUDCLNT_E_WRONG_ENDPOINT_TYPE.
                uint flags = StreamFlagsLoopback | StreamFlagsEventCallback;
                ThrowIfFailed(client.Initialize(ShareModeShared, flags, 0, 0, format, IntPtr.Zero),
                              "initialising the capture client");

                sampleReady = new AutoResetEvent(false);
                ThrowIfFailed(client.SetEventHandle(sampleReady.SafeWaitHandle.DangerousGetHandle()),
                              "attaching the capture event");

                ThrowIfFailed(client.GetService(ref IID_IAudioCaptureClient, out object service),
                              "obtaining the capture service");
                capture = (IAudioCaptureClient)service;

                ThrowIfFailed(client.Start(), "starting capture");

                running = true;
                worker = new Thread(CaptureLoop)
                {
                    IsBackground = true,
                    Name = "SoundSync process loopback",
                    Priority = ThreadPriority.AboveNormal
                };
                worker.Start();
            }
            finally
            {
                if (format != IntPtr.Zero) Marshal.FreeHGlobal(format);
                if (propVariant != IntPtr.Zero) Marshal.FreeHGlobal(propVariant);
                if (activationBlob != IntPtr.Zero) Marshal.FreeHGlobal(activationBlob);
            }
        }

        private void CaptureLoop()
        {
            Exception? ending = null;
            var scratch = new byte[64 * 1024];

            try
            {
                while (running)
                {
                    // Never wait forever: a target that never plays anything would otherwise
                    // park this thread for good.
                    if (sampleReady!.WaitOne(200) == false) continue;

                    while (running)
                    {
                        int hr = capture!.GetBuffer(out IntPtr data, out uint frames,
                                                    out uint flags, out _, out _);
                        if (hr != 0 || frames == 0) break;

                        int bytes = (int)frames * WaveFormat.BlockAlign;
                        if (scratch.Length < bytes) scratch = new byte[bytes];

                        if ((flags & BufferFlagsSilent) != 0) Array.Clear(scratch, 0, bytes);
                        else Marshal.Copy(data, scratch, 0, bytes);

                        capture.ReleaseBuffer(frames);
                        DataAvailable?.Invoke(this, new WaveInEventArgs(scratch, bytes));
                    }
                }
            }
            catch (Exception ex) { ending = ex; }
            finally { RecordingStopped?.Invoke(this, new StoppedEventArgs(ending)); }
        }

        public void StopRecording()
        {
            if (!running) return;
            running = false;
            sampleReady?.Set();
            try { worker?.Join(1000); } catch { }
            try { client?.Stop(); } catch { }
        }

        private static void ThrowIfFailed(int hr, string what)
        {
            if (hr >= 0) return;
            throw new InvalidOperationException(
                $"Pre-volume capture failed while {what}: 0x{hr:X8}. " +
                AudioErrors.DescribeHResult(hr, "the process loopback device"));
        }

        public void Dispose()
        {
            StopRecording();
            try { if (capture != null) Marshal.ReleaseComObject(capture); } catch { }
            try { if (client != null) Marshal.ReleaseComObject(client); } catch { }
            capture = null;
            client = null;
            sampleReady?.Dispose();
            sampleReady = null;
        }
    }
}
