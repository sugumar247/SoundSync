using System;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace SoundSync.Services
{
    /// <summary>
    /// Turns the raw WASAPI and COM failures into something a person can act on.
    /// Windows reports most audio problems as an opaque HRESULT or a bare
    /// "Value does not fall within the expected range", which tells the user nothing.
    /// </summary>
    public static class AudioErrors
    {
        // AUDCLNT_* error codes, from the Windows Core Audio headers.
        private const int UnsupportedFormat = unchecked((int)0x88890008);
        private const int DeviceInUse = unchecked((int)0x8889000A);
        private const int DeviceInvalidated = unchecked((int)0x88890004);
        private const int ExclusiveModeNotAllowed = unchecked((int)0x8889000F);
        private const int EndpointCreateFailed = unchecked((int)0x88890010);
        private const int ServiceNotRunning = unchecked((int)0x88890011);
        private const int BufferSizeError = unchecked((int)0x88890006);
        private const int CpuUsageExceeded = unchecked((int)0x88890017);
        private const int NotInitialized = unchecked((int)0x88890001);
        private const int AlreadyInitialized = unchecked((int)0x88890002);
        private const int NotFound = unchecked((int)0x80070490);
        private const int AccessDenied = unchecked((int)0x80070005);

        /// <summary>
        /// Explains why one output could not be started, naming the device and the concrete
        /// mismatch where there is one.
        /// </summary>
        public static string Explain(Exception error, string deviceName, WaveFormat captureFormat, MMDevice? device)
        {
            string outputFormat = "unknown";
            int outputChannels = 0;
            try
            {
                var mix = device?.AudioClient.MixFormat;
                if (mix != null)
                {
                    outputFormat = $"{mix.SampleRate} Hz / {mix.Channels} ch";
                    outputChannels = mix.Channels;
                }
            }
            catch { }

            string source = $"{captureFormat.SampleRate} Hz / {captureFormat.Channels} ch";
            string detail = Describe(error, deviceName);

            string mismatch = string.Empty;
            if (outputChannels > 0 && outputChannels != captureFormat.Channels)
            {
                mismatch = $"\nThe source is {captureFormat.Channels} channel and this output is {outputChannels} channel, " +
                           "so the channels have to be remapped.";
            }

            return $"{deviceName}\n{detail}\nSource: {source}   Output: {outputFormat}{mismatch}";
        }

        /// <summary>Plain-language description of a single audio failure.</summary>
        public static string Describe(Exception error, string deviceName)
        {
            // Unwrap the layers NAudio and the app add on top of the real cause.
            Exception root = error;
            while (root.InnerException != null) root = root.InnerException;

            if (root is COMException com) return DescribeHResult(com.ErrorCode, deviceName);
            if (root is ArgumentException)
            {
                return "Windows rejected the audio format for this output. This is what happens when the " +
                       "source and the output do not have the same number of channels, or the output was " +
                       "reconfigured while it was open.";
            }
            if (root is UnauthorizedAccessException)
            {
                return "Windows denied access to this device. Check Settings > Privacy > Microphone and " +
                       "app permissions, or whether another program has taken it in exclusive mode.";
            }
            if (root is InvalidOperationException) return root.Message;

            int hr = Marshal.GetHRForException(root);
            string known = DescribeHResult(hr, deviceName);
            return known.StartsWith("Windows reported") ? $"{root.GetType().Name}: {root.Message}" : known;
        }

        /// <summary>Plain-language description of a Windows audio HRESULT.</summary>
        public static string DescribeHResult(int hr, string deviceName) => hr switch
        {
            UnsupportedFormat =>
                "This output does not accept that audio format. Its sample rate or channel layout cannot " +
                "carry what the source device produces.",
            DeviceInUse =>
                "Another program is holding this device in exclusive mode, which locks everyone else out. " +
                "Close that program, or turn off \"Allow applications to take exclusive control\" in the " +
                "device properties in the Windows Sound panel.",
            DeviceInvalidated =>
                "The device disappeared while it was being opened - unplugged, disabled, or its driver " +
                "restarted. Press RE-SCAN HARDWARE.",
            ExclusiveModeNotAllowed =>
                "This device is set to refuse shared access, so only one program at a time can use it. " +
                "Turn off exclusive mode in its properties in the Windows Sound panel.",
            EndpointCreateFailed =>
                "Windows could not create the audio endpoint. Usually a driver problem - reinstall or " +
                "update the sound driver for this device.",
            ServiceNotRunning =>
                "The Windows Audio service is not running. Start \"Windows Audio\" in services.msc.",
            BufferSizeError =>
                "The requested buffer size is invalid for this device. Its driver wants a different size.",
            CpuUsageExceeded =>
                "Windows stopped the stream because the machine could not keep up. Close heavy programs, " +
                "or raise the buffer.",
            NotInitialized => "The audio client was used before being initialised - an internal ordering bug.",
            AlreadyInitialized => "The audio client was initialised twice - an internal ordering bug.",
            NotFound => $"{deviceName} was not found. It may have been unplugged or disabled.",
            AccessDenied => "Windows denied access to this device.",
            0 => "The operation failed without reporting a reason.",
            _ => $"Windows reported error 0x{hr:X8} on this device."
        };

        /// <summary>
        /// Why a sample rate change was refused. Windows gives no reason code here, so this
        /// names the causes actually observed, most likely first.
        /// </summary>
        public static string ExplainRateRejection(MMDevice device, int requestedRate)
        {
            string busy = string.Empty;
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    if (sessions[i].State != AudioSessionState.AudioSessionStateActive) continue;
                    try
                    {
                        var name = System.Diagnostics.Process.GetProcessById((int)sessions[i].GetProcessID).ProcessName;
                        busy += (busy.Length > 0 ? ", " : "") + name;
                    }
                    catch { }
                }
            }
            catch { }

            if (busy.Length > 0)
            {
                return $"Windows refused to switch this output to {requestedRate} Hz because it is playing " +
                       $"right now ({busy}). Stop that audio and try again.";
            }

            return $"Windows refused {requestedRate} Hz on this output. Its driver does not accept that rate " +
                   "in shared mode, or the device is busy. The rate list is what the hardware reports, and " +
                   "drivers sometimes advertise more than they will actually switch to.";
        }
    }
}
