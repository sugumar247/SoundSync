namespace SoundSync.Models
{
    /// <summary>Preferences that belong to the app rather than to one device.</summary>
    public class AppSettings
    {
        /// <summary>Start mirroring by itself when the app opens.</summary>
        public bool AutoConnectOnLaunch { get; set; }

        /// <summary>Show rows the user has hidden.</summary>
        public bool ShowHiddenDevices { get; set; }

        /// <summary>
        /// Optional key file the phone-link token is derived from. Empty means the app uses a
        /// random token kept in its own folder, which is the default.
        /// </summary>
        public string LinkTokenKeyFile { get; set; } = string.Empty;

        /// <summary>
        /// Undo the default device's volume on the mirrored signal, so each output's own
        /// Windows volume is the only thing that sets how loud it plays. On by default.
        /// </summary>
        public bool IndependentVolumes { get; set; } = true;

        /// <summary>
        /// Let listeners change this PC's outputs, not just hear them.
        ///
        /// On by default: the point of the page is to be a remote for the machine. Anyone
        /// holding the link gets it, so the header button turns it off for a link shared
        /// with people who should only listen.
        /// </summary>
        public bool AllowRemoteControl { get; set; } = true;

        // ---- window geometry --------------------------------------------------------
        // Zero width means "never saved", so the app sizes itself against the monitor the
        // first time and remembers whatever the user settles on after that.

        public double WindowWidth { get; set; }
        public double WindowHeight { get; set; }
        public double WindowLeft { get; set; } = double.NaN;
        public double WindowTop { get; set; } = double.NaN;
        public bool WindowMaximized { get; set; }
    }
}
