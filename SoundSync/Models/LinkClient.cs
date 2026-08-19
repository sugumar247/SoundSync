using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SoundSync.Models
{
    /// <summary>One browser or player currently listening to the network stream.</summary>
    public class LinkClient : INotifyPropertyChanged
    {
        public string Address { get; set; } = string.Empty;

        /// <summary>Friendly device name worked out from the User-Agent.</summary>
        public string DeviceName { get; set; } = "Unknown device";

        /// <summary>"browser" for the web page, "player" for VLC and similar.</summary>
        public string Kind { get; set; } = "browser";

        public DateTime ConnectedAt { get; set; } = DateTime.Now;

        public string ConnectedAtText => ConnectedAt.ToString("HH:mm:ss");

        public string Summary => $"{DeviceName}  ·  {Address}  ·  since {ConnectedAtText}";

        /// <summary>Set by the streamer so a volume change here reaches that listener.</summary>
        public Action<LinkClient>? VolumeChanged { get; set; }

        private float _volume = 1.0f;
        /// <summary>
        /// Playback level for this listener, 0..1.5. Applied in the browser's own gain node,
        /// so one listener turning down changes nothing for the others or for the PC.
        /// </summary>
        public float Volume
        {
            get => _volume;
            set
            {
                float clamped = Math.Clamp(value, 0f, 1.5f);
                if (Math.Abs(_volume - clamped) < 0.001f) return;
                _volume = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VolumePercent));
                VolumeChanged?.Invoke(this);
            }
        }

        public string VolumePercent => $"{Volume * 100:F0}%";

        private bool _syncWithDefault;
        /// <summary>
        /// Follow the PC's default device volume, keeping the ratio set here.
        ///
        /// Off by default: a listener's volume is its own. Ticking it - from the PC or from
        /// the page, the two stay in step - makes the PC's volume carry this one along.
        /// </summary>
        public bool SyncVolumeWithDefault
        {
            get => _syncWithDefault;
            set
            {
                if (_syncWithDefault == value) return;
                _syncWithDefault = value;
                OnPropertyChanged();
                SyncChanged?.Invoke(this);
            }
        }

        /// <summary>Set by the streamer so a change here reaches that listener's page.</summary>
        public Action<LinkClient>? SyncChanged { get; set; }

        /// <summary>Ratio captured when the sync was switched on.</summary>
        public float VolumeRatioToDefault { get; set; } = 1.0f;

        /// <summary>Only a browser can be told what to do; a plain player cannot.</summary>
        public bool IsControllable => Kind == "browser";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>
        /// Turns a User-Agent into something a person recognises. Deliberately coarse: the
        /// point is telling one listener apart from another, not fingerprinting them.
        /// </summary>
        public static string DescribeUserAgent(string? userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent)) return "Unknown device";
            string ua = userAgent;

            bool Has(string s) => ua.Contains(s, StringComparison.OrdinalIgnoreCase);

            // Media players first: they rarely pretend to be browsers.
            if (Has("VLC")) return "VLC player";
            if (Has("foobar")) return "foobar2000";
            if (Has("Winamp")) return "Winamp";
            if (Has("MPV") || Has("mpv")) return "mpv player";
            if (Has("Lavf") || Has("FFmpeg")) return "FFmpeg / Lavf";
            if (Has("Kodi")) return "Kodi";
            if (Has("AppleCoreMedia")) return "Apple media player";
            if (Has("Music Player Daemon")) return "MPD";

            string platform =
                Has("iPhone") ? "iPhone" :
                Has("iPad") ? "iPad" :
                Has("iPod") ? "iPod" :
                Has("Android") ? "Android" :
                Has("Windows NT") ? "Windows PC" :
                Has("Macintosh") || Has("Mac OS X") ? "Mac" :
                Has("CrOS") ? "Chromebook" :
                Has("Linux") ? "Linux PC" : "Unknown device";

            // Order matters: Edge and Chrome both claim Safari, Chrome also claims Safari.
            string browser =
                Has("Edg/") ? "Edge" :
                Has("OPR/") || Has("Opera") ? "Opera" :
                Has("Firefox") ? "Firefox" :
                Has("SamsungBrowser") ? "Samsung Internet" :
                Has("Chrome") ? "Chrome" :
                Has("Safari") ? "Safari" : string.Empty;

            return browser.Length > 0 ? $"{platform} · {browser}" : platform;
        }

        /// <summary>True when the User-Agent looks like a media player rather than a browser.</summary>
        public static bool LooksLikePlayer(string? userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent)) return true;
            string ua = userAgent;
            return ua.Contains("VLC", StringComparison.OrdinalIgnoreCase)
                || ua.Contains("foobar", StringComparison.OrdinalIgnoreCase)
                || ua.Contains("Winamp", StringComparison.OrdinalIgnoreCase)
                || ua.Contains("mpv", StringComparison.OrdinalIgnoreCase)
                || ua.Contains("Lavf", StringComparison.OrdinalIgnoreCase)
                || ua.Contains("FFmpeg", StringComparison.OrdinalIgnoreCase)
                || ua.Contains("Kodi", StringComparison.OrdinalIgnoreCase)
                || ua.Contains("AppleCoreMedia", StringComparison.OrdinalIgnoreCase);
        }
    }
}
