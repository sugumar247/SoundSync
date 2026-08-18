using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundSync.Services
{
    /// <summary>
    /// Reads and changes Windows' own audio settings for an endpoint: which device is the
    /// system default, and the shared-mode sample rate ("Default Format" in the Sound
    /// control panel).
    ///
    /// Both go through IPolicyConfig, the undocumented COM interface the Sound control
    /// panel itself uses. Verified on this machine to work without administrator rights.
    /// </summary>
    public static class SystemAudioConfig
    {
        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class CPolicyConfigClient { }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr fmt);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int bDefault, out IntPtr fmt);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFmt, IntPtr mixFmt);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int bDefault, out long a, out long b);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, ref long period);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr pv);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr pv);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
        }

        /// <summary>Rates offered in the picker, in the order the Sound panel lists them.</summary>
        private static readonly int[] CandidateRates = { 44100, 48000, 88200, 96000, 176400, 192000 };

        private static IPolicyConfig? Create()
        {
            try { return (IPolicyConfig)new CPolicyConfigClient(); }
            catch { return null; }
        }

        /// <summary>Current shared-mode sample rate of the endpoint, or 0 if unreadable.</summary>
        public static int GetSampleRate(MMDevice device)
        {
            try
            {
                var pc = Create();
                if (pc == null) return 0;
                if (pc.GetDeviceFormat(device.ID, 0, out IntPtr fmt) != 0 || fmt == IntPtr.Zero) return 0;
                return Marshal.ReadInt32(fmt, 4);
            }
            catch { return 0; }
        }

        /// <summary>
        /// Rates the hardware reports as playable. Probed in exclusive mode, which is the
        /// only mode that answers honestly - shared mode only ever accepts the current
        /// mix format. The current rate is always included so the picker can show it.
        /// </summary>
        public static List<int> GetSupportedRates(MMDevice device)
        {
            var result = new List<int>();
            int current = GetSampleRate(device);

            int[] channelOptions;
            try { channelOptions = new[] { device.AudioClient.MixFormat.Channels, 2 }; }
            catch { channelOptions = new[] { 2 }; }

            foreach (int rate in CandidateRates)
            {
                bool ok = false;
                foreach (int channels in channelOptions)
                {
                    if (ok) break;
                    foreach (int bits in new[] { 16, 24, 32 })
                    {
                        try
                        {
                            if (device.AudioClient.IsFormatSupported(
                                    AudioClientShareMode.Exclusive, new WaveFormat(rate, bits, channels)))
                            {
                                ok = true;
                                break;
                            }
                        }
                        catch { }
                    }
                }
                if (ok) result.Add(rate);
            }

            if (current > 0 && !result.Contains(current)) result.Add(current);
            result.Sort();
            return result;
        }

        /// <summary>
        /// Changes the endpoint's shared-mode sample rate, keeping its current channel
        /// count and bit depth. Returns true when Windows accepted and applied it.
        /// </summary>
        public static bool SetSampleRate(MMDevice device, int sampleRate)
        {
            IntPtr wanted = IntPtr.Zero;
            try
            {
                var pc = Create();
                if (pc == null) return false;

                if (pc.GetDeviceFormat(device.ID, 0, out IntPtr currentFmt) != 0 || currentFmt == IntPtr.Zero)
                    return false;

                // Clone the driver's own format and patch only the rate. Building a fresh
                // WAVEFORMATEXTENSIBLE means guessing the channel mask and subformat, and
                // NVIDIA HDMI and Realtek both reject a guess outright - cloning keeps
                // whatever those drivers already accepted.
                wanted = CloneWithSampleRate(currentFmt, sampleRate);
                if (pc.SetDeviceFormat(device.ID, wanted, wanted) != 0) return false;

                // Confirm Windows really took it rather than trusting the return code.
                System.Threading.Thread.Sleep(400);
                if (pc.GetDeviceFormat(device.ID, 0, out IntPtr after) != 0 || after == IntPtr.Zero)
                    return false;
                return Marshal.ReadInt32(after, 4) == sampleRate;
            }
            catch { return false; }
            finally { if (wanted != IntPtr.Zero) Marshal.FreeHGlobal(wanted); }
        }

        /// <summary>
        /// Copies a WAVEFORMAT(EX/EXTENSIBLE) verbatim, changing only the sample rate and
        /// the average bytes per second that must follow from it.
        /// </summary>
        private static IntPtr CloneWithSampleRate(IntPtr source, int sampleRate)
        {
            short cbSize = Marshal.ReadInt16(source, 16);
            int total = 18 + cbSize;

            var bytes = new byte[total];
            Marshal.Copy(source, bytes, 0, total);

            IntPtr copy = Marshal.AllocHGlobal(total);
            Marshal.Copy(bytes, 0, copy, total);

            short blockAlign = Marshal.ReadInt16(source, 12);
            Marshal.WriteInt32(copy, 4, sampleRate);
            Marshal.WriteInt32(copy, 8, sampleRate * blockAlign);
            return copy;
        }

        /// <summary>Makes this endpoint the system default for all three roles.</summary>
        public static bool SetAsDefault(MMDevice device)
        {
            try
            {
                var pc = Create();
                if (pc == null) return false;
                // 0 = eConsole, 1 = eMultimedia, 2 = eCommunications
                for (int role = 0; role <= 2; role++)
                    if (pc.SetDefaultEndpoint(device.ID, role) != 0) return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>WAVEFORMATEXTENSIBLE in unmanaged memory: 18 byte header + 22 byte tail.</summary>
        private static IntPtr BuildWaveFormatExtensible(int sampleRate, int channels, int bits, uint channelMask)
        {
            IntPtr p = Marshal.AllocHGlobal(40);
            int blockAlign = channels * bits / 8;
            Marshal.WriteInt16(p, 0, unchecked((short)0xFFFE));   // WAVE_FORMAT_EXTENSIBLE
            Marshal.WriteInt16(p, 2, (short)channels);
            Marshal.WriteInt32(p, 4, sampleRate);
            Marshal.WriteInt32(p, 8, sampleRate * blockAlign);
            Marshal.WriteInt16(p, 12, (short)blockAlign);
            Marshal.WriteInt16(p, 14, (short)bits);
            Marshal.WriteInt16(p, 16, 22);                        // cbSize
            Marshal.WriteInt16(p, 18, (short)bits);               // wValidBitsPerSample
            Marshal.WriteInt32(p, 20, (int)channelMask);
            var subFormat = bits <= 16
                ? new Guid("00000001-0000-0010-8000-00aa00389b71")   // KSDATAFORMAT_SUBTYPE_PCM
                : new Guid("00000003-0000-0010-8000-00aa00389b71");  // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
            Marshal.Copy(subFormat.ToByteArray(), 0, p + 24, 16);
            return p;
        }
    }
}
