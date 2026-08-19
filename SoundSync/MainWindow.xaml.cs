using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SoundSync.Models;
using SoundSync.Services;
using SoundSync.Services.Providers;


namespace SoundSync
{
    public partial class MainWindow : Window
    {
        private MMDeviceEnumerator? enumerator;
        private List<MMDevice>? allDevices;

        /// <summary>Every row, including the ones filtered out of the visible list.</summary>
        private List<DeviceItem> allItems = new List<DeviceItem>();
        private AppSettings appSettings = new AppSettings();

        private readonly IAudioEngine audioEngine = new AudioEngine();
        private bool isConnected => audioEngine.IsConnected;
        private bool isMuted = false;

        // SoundSync Link Wi-Fi Stream Server
        private INetworkStreamer? linkServer;
        private string currentStreamUrl = string.Empty;

        // Default device peak level tracking timer
        private System.Windows.Threading.DispatcherTimer? defaultDevicePeakTimer;

        // System Tray Components
        private Hardcodet.Wpf.TaskbarNotification.TaskbarIcon? notifyIcon;

        // Settings Profile Path (Updated to ensure compatibility with Self-Contained Single-File Executables)
        private readonly string profilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoundSync",
            "settings_profile.json"
        );

        public MainWindow()
        {
            // Ensure data directory exists before running file configuration components
            try
            {
                string? directory = Path.GetDirectoryName(profilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
            catch { }

            InitializeComponent();
            LoadDevices();
            InitializeNotifyIcon();
            this.StateChanged += MainWindow_StateChanged;
            this.Closing += (s, e) => SaveWindowGeometry();
            this.Loaded += MainWindow_Loaded;

            ApplyTheme(false);
        }

        private string latestReleaseUrl = "https://github.com/sugumar247/SoundSync/releases";

        /// <summary>
        /// Sizes the window against the monitor rather than trusting a fixed number.
        ///
        /// The XAML default is a reasonable size on a 1080p screen, but on a 4K panel at 100%
        /// scaling it occupies about a third of the width and the text is too small to read
        /// across the room. Taking a share of the work area gives a window that looks the same
        /// on every monitor, and the clamps stop it from becoming silly in either direction.
        /// </summary>
        /// <summary>
        /// Puts the window back where it was last time, or sizes it against the monitor on
        /// the very first run. A saved position is only honoured if it still lands on a
        /// screen that exists - unplugging a monitor should not hide the window off-screen.
        /// </summary>
        private void RestoreWindowGeometry()
        {
            if (appSettings.WindowWidth < 200 || appSettings.WindowHeight < 200)
            {
                SizeToScreen();
                return;
            }

            Width = Math.Min(appSettings.WindowWidth, SystemParameters.VirtualScreenWidth);
            Height = Math.Min(appSettings.WindowHeight, SystemParameters.VirtualScreenHeight);

            if (!double.IsNaN(appSettings.WindowLeft) && !double.IsNaN(appSettings.WindowTop)
                && IsOnAVisibleScreen(appSettings.WindowLeft, appSettings.WindowTop))
            {
                Left = appSettings.WindowLeft;
                Top = appSettings.WindowTop;
            }
            else
            {
                CentreOnWorkArea();
            }

            if (appSettings.WindowMaximized) WindowState = WindowState.Maximized;
        }

        private static bool IsOnAVisibleScreen(double left, double top)
        {
            double minX = SystemParameters.VirtualScreenLeft;
            double minY = SystemParameters.VirtualScreenTop;
            double maxX = minX + SystemParameters.VirtualScreenWidth;
            double maxY = minY + SystemParameters.VirtualScreenHeight;

            // Require a chunk of the title bar to be reachable, not just one pixel.
            return left > minX - 200 && left < maxX - 100 && top >= minY && top < maxY - 60;
        }

        private void CentreOnWorkArea()
        {
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
            Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2;
        }

        /// <summary>Remembers the current size and position for next time.</summary>
        private void SaveWindowGeometry()
        {
            try
            {
                appSettings.WindowMaximized = WindowState == WindowState.Maximized;

                // RestoreBounds holds the pre-maximise rectangle, which is what should come
                // back when the user un-maximises later.
                var bounds = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;

                if (bounds.Width >= 200 && bounds.Height >= 200)
                {
                    appSettings.WindowWidth = bounds.Width;
                    appSettings.WindowHeight = bounds.Height;
                    appSettings.WindowLeft = bounds.Left;
                    appSettings.WindowTop = bounds.Top;
                }

                SaveAppSettings();
            }
            catch { }
        }

        /// <summary>Drops the saved geometry and sizes against the monitor again.</summary>
        private void ResetWindowSize()
        {
            WindowState = WindowState.Normal;
            Width = MinWidth;
            Height = MinHeight;
            SizeToScreen();

            appSettings.WindowWidth = 0;
            appSettings.WindowHeight = 0;
            appSettings.WindowLeft = double.NaN;
            appSettings.WindowTop = double.NaN;
            appSettings.WindowMaximized = false;
            SaveAppSettings();

            Show();
            Activate();

            StatusText.Text = $"Window reset to {Width:F0} x {Height:F0}, sized against this monitor.";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusSuccessBrush");
        }

        private void SizeToScreen()
        {
            try
            {
                double availableWidth = SystemParameters.WorkArea.Width;
                double availableHeight = SystemParameters.WorkArea.Height;
                if (availableWidth <= 0 || availableHeight <= 0) return;

                double wanted = Math.Clamp(availableWidth * 0.60, MinWidth, 2400);
                double wantedHeight = Math.Clamp(availableHeight * 0.80, MinHeight, 1800);

                // Never shrink below what the XAML asked for on a small screen.
                Width = Math.Max(Width, Math.Min(wanted, availableWidth - 40));
                Height = Math.Max(Height, Math.Min(wantedHeight, availableHeight - 40));

                Left = SystemParameters.WorkArea.Left + (availableWidth - Width) / 2;
                Top = SystemParameters.WorkArea.Top + (availableHeight - Height) / 2;
            }
            catch { }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadAppSettings();
            RestoreWindowGeometry();

            // Commands forwarded by a second launch, such as the taskbar jump list entry.
            SingleInstanceCommands.StartListening(command => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (command == SingleInstanceCommands.ResetWindowArgument) ResetWindowSize();
            })));
            LinkAuth.Configure(appSettings.LinkTokenKeyFile);
            RefreshLinkTokenSource();
            ReloadDevices();
            CheckForUpdatesAsync();

            // Reconnect by itself when the user asked for it, or when Windows launched us
            // at sign-in with the auto-connect argument.
            if (appSettings.AutoConnectOnLaunch || StartupManager.LaunchedForAutoConnect())
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!isConnected && allItems.Any(i => i.IsSelected && !i.IsDefaultDevice))
                        ConnectButton_Click(this, new RoutedEventArgs());
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private System.Windows.Threading.DispatcherTimer? autoAlignTimer;
        private bool autoAlignWarned;

        /// <summary>
        /// Every two seconds, pulls the auto-aligned outputs level with the slowest one.
        ///
        /// Only the mirrored outputs can be compared: their real delay is measurable from how
        /// much audio is waiting in each buffer. The default device is not in the running - it
        /// plays straight from Windows, so there is nothing to measure and nothing to delay.
        /// </summary>
        private void AutoAlignTick(object? sender, EventArgs e)
        {
            var aligned = allItems
                .Where(i => i.AutoAlignDelay && i.IsSelected && i.DelayProvider != null && i.OutputBuffer != null)
                .ToList();
            if (aligned.Count == 0) return;

            // Latency without the delay already applied, so the target does not drift upward.
            double Raw(DeviceItem i) => Math.Max(0, i.MeasuredLatencyMs - Math.Max(0, i.Delay));

            var candidates = allItems
                .Where(i => i.IsSelected && i.DelayProvider != null && i.OutputBuffer != null)
                .ToList();

            // With a single mirrored output there is nothing to align against: the default
            // device is the other half of the pair and it cannot be measured or delayed.
            // Say so once rather than sitting there doing nothing.
            if (candidates.Count < 2)
            {
                if (!autoAlignWarned)
                {
                    autoAlignWarned = true;
                    StatusText.Text = "Auto-align needs at least two mirrored outputs to compare. " +
                                      "With one, there is nothing to line it up against - the default device " +
                                      "cannot be measured or delayed.";
                    StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusMutedBrush");
                }
                return;
            }
            autoAlignWarned = false;

            double slowest = candidates.Max(Raw);

            foreach (var item in aligned)
            {
                int wanted = (int)Math.Round(Math.Clamp(slowest - Raw(item), 0, 500));
                // Ignore sub-3 ms churn: buffers breathe, and nudging the delay every tick
                // would be audible as constant micro-corrections.
                if (Math.Abs(wanted - item.Delay) < 3) continue;
                item.Delay = wanted;
            }

            UpdateRelativeDelays();
        }

        private void StartAutoAlignTimer()
        {
            autoAlignTimer ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            autoAlignTimer.Tick -= AutoAlignTick;
            autoAlignTimer.Tick += AutoAlignTick;
            autoAlignTimer.Start();
        }

        private void StopAutoAlignTimer() => autoAlignTimer?.Stop();

        // ---- volume that follows the default device ---------------------------------

        private MMDevice? volumeSyncSource;
        private AudioEndpointVolumeNotificationDelegate? volumeSyncHandler;

        /// <summary>
        /// Listens to the default device's Windows volume and moves every output that asked
        /// to follow it, keeping the ratio each one had when it was switched on.
        /// </summary>
        private void AttachVolumeSync()
        {
            DetachVolumeSync();
            var defaultItem = allItems.FirstOrDefault(i => i.IsDefaultDevice);
            if (defaultItem == null) return;

            try
            {
                volumeSyncSource = defaultItem.Device;
                volumeSyncHandler = data => Dispatcher.BeginInvoke(new Action(() => ApplyVolumeSync(data.MasterVolume)));
                volumeSyncSource.AudioEndpointVolume.OnVolumeNotification += volumeSyncHandler;
            }
            catch { volumeSyncSource = null; volumeSyncHandler = null; }
        }

        private void DetachVolumeSync()
        {
            try
            {
                if (volumeSyncSource != null && volumeSyncHandler != null)
                    volumeSyncSource.AudioEndpointVolume.OnVolumeNotification -= volumeSyncHandler;
            }
            catch { }
            volumeSyncSource = null;
            volumeSyncHandler = null;
        }

        private void ApplyVolumeSync(float defaultVolume)
        {
            // Loopback capture is post-volume, so the mirrored signal already carries the
            // default device's attenuation. Divide it back out and each output's own volume
            // becomes the only thing setting its level.
            if (isConnected)
            {
                audioEngine.SetDefaultDeviceVolume(defaultVolume);
                audioEngine.ApplyMakeUpGain(allItems, defaultVolume, appSettings.IndependentVolumes);
            }
            ApplyRemoteVolumeSync();

            foreach (var item in allItems.Where(i => i.SyncVolumeWithDefault && !i.IsDefaultDevice))
            {
                float wanted = Math.Clamp(defaultVolume * item.VolumeRatioToDefault, 0f, 1f);
                item.SystemVolume = wanted;
            }
        }

        private void SyncVolumeCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.CheckBox box) return;
            if (box.DataContext is not DeviceItem item) return;

            if (item.SyncVolumeWithDefault)
            {
                // Capture the balance the user already has, so switching this on does not
                // jump the speaker to a different level.
                var defaultItem = allItems.FirstOrDefault(i => i.IsDefaultDevice);
                float reference = defaultItem?.SystemVolume ?? 0f;
                item.VolumeRatioToDefault = reference > 0.001f
                    ? Math.Clamp(item.SystemVolume / reference, 0f, 4f)
                    : 1.0f;
            }

            SaveCurrentProfile();
        }

        private void AutoAlignCheck_Changed(object sender, RoutedEventArgs e)
        {
            SaveCurrentProfile();
            if (isConnected) UpdateRelativeDelays();
        }

        private System.Windows.Threading.DispatcherTimer? latencyTimer;

        /// <summary>Refreshes the measured delay readings four times a second.</summary>
        private void StartLatencyTimer()
        {
            latencyTimer ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            latencyTimer.Tick -= LatencyTimer_Tick;
            latencyTimer.Tick += LatencyTimer_Tick;
            latencyTimer.Start();
        }

        private void LatencyTimer_Tick(object? sender, EventArgs e)
        {
            foreach (var item in allItems) item.RefreshMeasuredLatency();
        }

        private void StopLatencyTimer()
        {
            latencyTimer?.Stop();
            foreach (var item in allItems) item.RefreshMeasuredLatency();
        }

        /// <summary>Re-applies the make-up gain from the default device's current volume.</summary>
        private void RefreshMakeUpGain()
        {
            var defaultItem = allItems.FirstOrDefault(i => i.IsDefaultDevice);
            float reference = defaultItem?.SystemVolume ?? 1.0f;

            // The meters always correct for the master volume, even when the make-up gain
            // itself is off, so the bars keep showing the material rather than a knob.
            audioEngine.SetDefaultDeviceVolume(reference);
            audioEngine.ApplyMakeUpGain(allItems, reference, appSettings.IndependentVolumes);
        }

        private void IndependentVolumesButton_Click(object sender, RoutedEventArgs e)
        {
            appSettings.IndependentVolumes = !appSettings.IndependentVolumes;
            SaveAppSettings();
            RefreshHeaderToggles();
            if (isConnected) RefreshMakeUpGain();

            StatusText.Text = appSettings.IndependentVolumes
                ? "Mirrors now get the full-strength signal - their own Windows volume sets the level."
                : "Mirrors now inherit the default device's volume as well as their own.";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusSuccessBrush");
        }

        // ---- remote control from the web page ----------------------------------------

        /// <summary>
        /// Sends the page a snapshot of the outputs, so it can show the same list the PC
        /// shows. Only what the page needs to draw and drive a row goes over the wire.
        /// </summary>
        private void PushDeviceSnapshot()
        {
            if (linkServer == null || !isConnected) return;

            var rows = allItems
                .Where(i => !i.IsHidden)
                .Select(i => new
                {
                    id = i.Device.ID,
                    name = i.Name,
                    isDefault = i.IsDefaultDevice,
                    selected = i.IsSelected,
                    volume = Math.Round(i.SystemVolume, 3),
                    delay = i.Delay,
                    badge = i.ConnectionBadge,
                    editable = i.CanProcessAudio
                })
                .ToList();

            var payload = new
            {
                type = "devices",
                controllable = appSettings.AllowRemoteControl,
                devices = rows
            };

            try { linkServer.SendToAll(JsonSerializer.Serialize(payload)); } catch { }
        }

        /// <summary>Applies a command the page sent back. Ignored unless remote control is on.</summary>
        private void HandleRemoteCommand(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) return;
                string type = typeProp.GetString() ?? string.Empty;

                bool ownSettings = type == "listenerVolume" || type == "listenerSync";
                if (!ownSettings && !appSettings.AllowRemoteControl) return;

                // A listener changing its OWN volume or sync is not remote control of the
                // PC: it only affects that listener. It works whether or not the machine is
                // handing out control of its outputs.
                if (type == "listenerVolume" || type == "listenerSync")
                {
                    var self = linkServer?.GetClients().FirstOrDefault(c => c.IsControllable);
                    if (self == null) return;

                    if (type == "listenerVolume" && root.TryGetProperty("value", out var lv))
                    {
                        self.SyncVolumeWithDefault = false;   // touching it by hand breaks the tie
                        self.Volume = (float)Math.Clamp(lv.GetDouble(), 0, 1.5);
                    }
                    else if (type == "listenerSync" && root.TryGetProperty("value", out var ls))
                    {
                        bool wanted = ls.GetBoolean();
                        if (wanted)
                        {
                            var reference = allItems.FirstOrDefault(i => i.IsDefaultDevice)?.SystemVolume ?? 0f;
                            self.VolumeRatioToDefault = reference > 0.001f
                                ? Math.Clamp(self.Volume / reference, 0f, 4f)
                                : 1.0f;
                        }
                        self.SyncVolumeWithDefault = wanted;
                        ApplyRemoteVolumeSync();
                    }

                    RefreshRemoteListeners();
                    return;
                }

                if (type == "refresh") { PushDeviceSnapshot(); return; }
                if (!root.TryGetProperty("id", out var idProp)) return;

                string id = idProp.GetString() ?? string.Empty;
                var item = allItems.FirstOrDefault(i => i.Device.ID == id);
                if (item == null) return;

                switch (type)
                {
                    case "volume" when root.TryGetProperty("value", out var v):
                        item.SystemVolume = (float)Math.Clamp(v.GetDouble(), 0, 1);
                        break;

                    case "delay" when root.TryGetProperty("value", out var d) && item.CanEditDelay:
                        item.Delay = (int)Math.Clamp(d.GetDouble(), 0, 500);
                        UpdateRelativeDelays();
                        break;

                    case "select" when root.TryGetProperty("value", out var sel) && !item.IsDefaultDevice:
                        item.IsSelected = sel.GetBoolean();
                        SaveCurrentProfile();
                        break;
                }

                PushDeviceSnapshot();
            }
            catch { }
        }

        private void RemoteControlButton_Click(object sender, RoutedEventArgs e)
        {
            appSettings.AllowRemoteControl = !appSettings.AllowRemoteControl;
            SaveAppSettings();
            RefreshHeaderToggles();
            PushDeviceSnapshot();

            StatusText.Text = appSettings.AllowRemoteControl
                ? "Listeners can now change this PC's outputs. Anyone holding the link can do it."
                : "Listeners can see the outputs but not change them.";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusSuccessBrush");
        }

        // ---- recovering from a device format change ----------------------------------

        private bool restartingAfterFormatChange;

        /// <summary>
        /// Restarts mirroring after an endpoint's sample rate changed.
        ///
        /// Windows tears down and rebuilds the audio engine for a device whose format
        /// changes. Any stream open on it dies with it - and if it was the default device,
        /// the loopback capture everything is fed from dies too, so every output goes quiet
        /// while the app still believes it is connected. Rebuilding the session is the only
        /// way back, so do it here rather than leaving the user to press CONNECT twice.
        /// </summary>
        private void HandleFormatChanged(DeviceItem changed)
        {
            if (!isConnected || restartingAfterFormatChange) return;
            restartingAfterFormatChange = true;

            StatusText.Text = $"{changed.Name} changed to {changed.SampleRate} Hz - restarting mirroring...";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusMutedBrush");

            // Give Windows a moment to finish rebuilding the endpoint before reopening it.
            var settle = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(900)
            };
            settle.Tick += (s, e) =>
            {
                settle.Stop();
                try
                {
                    Disconnect();
                    ConnectButton_Click(this, new RoutedEventArgs());
                    if (isConnected)
                    {
                        StatusText.Text = $"Mirroring restarted at {changed.SampleRate} Hz.";
                        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusSuccessBrush");
                    }
                }
                catch (Exception ex)
                {
                    StatusText.Text = "Could not restart after the format change: " + ex.Message;
                    StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusErrorBrush");
                }
                finally { restartingAfterFormatChange = false; }
            };
            settle.Start();
        }

        // ---- listeners on the network ------------------------------------------------

        /// <summary>Redraws the remote listener list. Called whenever someone joins or leaves.</summary>
        private void RefreshRemoteListeners()
        {
            var listeners = linkServer?.GetClients() ?? new List<LinkClient>();
            RemoteList.ItemsSource = null;
            RemoteList.ItemsSource = listeners;

            RemotePanel.Visibility = listeners.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RemoteHeader.Text = listeners.Count == 1
                ? "REMOTE LISTENERS  -  1 connected"
                : $"REMOTE LISTENERS  -  {listeners.Count} connected";

            ApplyRemoteVolumeSync();
            PushDeviceSnapshot();
        }

        private void RemoteSync_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.CheckBox box) return;
            if (box.DataContext is not LinkClient client) return;

            if (client.SyncVolumeWithDefault)
            {
                // Capture the balance it already has, so ticking the box does not jump it.
                var defaultItem = allItems.FirstOrDefault(i => i.IsDefaultDevice);
                float reference = defaultItem?.SystemVolume ?? 0f;
                client.VolumeRatioToDefault = reference > 0.001f
                    ? Math.Clamp(client.Volume / reference, 0f, 4f)
                    : 1.0f;
            }
            ApplyRemoteVolumeSync();
        }

        /// <summary>Moves every synced listener to match the default device's volume.</summary>
        private void ApplyRemoteVolumeSync()
        {
            var defaultItem = allItems.FirstOrDefault(i => i.IsDefaultDevice);
            if (defaultItem == null) return;
            float reference = defaultItem.SystemVolume;

            foreach (var client in linkServer?.GetClients() ?? new List<LinkClient>())
            {
                if (!client.SyncVolumeWithDefault || !client.IsControllable) continue;
                client.Volume = Math.Clamp(reference * client.VolumeRatioToDefault, 0f, 1.5f);
            }
        }

        // ---- phone link address -----------------------------------------------------

        /// <summary>Shows the stream address in a box the user can select, copy or open.</summary>
        private void ShowLinkPanel(string url)
        {
            currentStreamUrl = url;
            LinkUrlBox.Text = url;
        }

        private void HideLinkPanel()
        {
            currentStreamUrl = string.Empty;
            LinkUrlBox.Text = "(connect to get the address)";
        }

        /// <summary>Shows which key file is in use, or blank for the random stored token.</summary>
        private void RefreshLinkTokenSource()
        {
            LinkKeyFileBox.Text = appSettings.LinkTokenKeyFile;
            LinkTokenSourceText.Text = LinkAuth.SourceDescription;
        }

        private void ApplyLinkTokenKeyFile(string path)
        {
            appSettings.LinkTokenKeyFile = path;
            SaveAppSettings();
            LinkAuth.Configure(path);
            RefreshLinkTokenSource();

            if (isConnected)
            {
                var ip = GetLocalIPAddress();
                ShowLinkPanel($"http://{ip}:8090/?t={LinkAuth.Token}");
                StatusText.Text = "Phone link token changed - the address above is the new one.";
                StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusSuccessBrush");
            }
        }

        private void BrowseKeyFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Pick the key file to derive the phone link token from",
                CheckFileExists = true,
                InitialDirectory = Path.GetDirectoryName(LinkAuth.SuggestedKeyFile) ?? string.Empty,
                FileName = Path.GetFileName(LinkAuth.SuggestedKeyFile),
                Filter = "All files (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) == true) ApplyLinkTokenKeyFile(dialog.FileName);
        }

        private void UseRandomToken_Click(object sender, RoutedEventArgs e)
            => ApplyLinkTokenKeyFile(string.Empty);

        private void CopyLink_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(LinkUrlBox.Text)) return;
            try
            {
                System.Windows.Clipboard.SetText(LinkUrlBox.Text);
                StatusText.Text = "Phone link copied to the clipboard.";
                StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusSuccessBrush");
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not reach the clipboard: " + ex.Message +
                                  " Select the address and copy it by hand.";
                StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusErrorBrush");
            }
        }

        private void OpenLink_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(LinkUrlBox.Text)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = LinkUrlBox.Text,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not open a browser: " + ex.Message;
                StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusErrorBrush");
            }
        }

        // ---- app-level preferences -------------------------------------------------

        private string AppSettingsPath => Path.Combine(
            Path.GetDirectoryName(profilePath) ?? string.Empty, "app_settings.json");

        private void LoadAppSettings()
        {
            try
            {
                if (!File.Exists(AppSettingsPath)) return;
                appSettings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppSettingsPath))
                              ?? new AppSettings();
            }
            catch { appSettings = new AppSettings(); }
        }

        private void SaveAppSettings()
        {
            try
            {
                File.WriteAllText(AppSettingsPath,
                    JsonSerializer.Serialize(appSettings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // ---- hiding rows -----------------------------------------------------------

        /// <summary>
        /// Rebuilds the device list from Windows and puts the saved state back on it.
        ///
        /// The order is the whole point: LoadDevices creates fresh rows with IsHidden false,
        /// so the filter has to run *after* the profile restores those flags. Doing it the
        /// other way round made every hidden device reappear on refresh.
        /// </summary>
        private void ReloadDevices()
        {
            LoadDevices();
            LoadSavedProfile();
            ApplyDeviceFilter();
            RefreshHeaderToggles();
            AttachVolumeSync();
        }

        /// <summary>Shows every row, or only the ones the user has not hidden.</summary>
        private void ApplyDeviceFilter()
        {
            var visible = appSettings.ShowHiddenDevices
                ? allItems
                : allItems.Where(i => !i.IsHidden).ToList();

            DeviceListBox.ItemsSource = null;
            DeviceListBox.ItemsSource = visible;

            int hidden = allItems.Count(i => i.IsHidden);
            ShowHiddenButton.Content = appSettings.ShowHiddenDevices
                ? $"HIDING OFF ({hidden})"
                : hidden > 0 ? $"SHOW HIDDEN ({hidden})" : "SHOW HIDDEN";
        }

        private void HideDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button button) return;
            if (button.Tag is not DeviceItem item) return;

            item.IsHidden = !item.IsHidden;
            SaveCurrentProfile();
            ApplyDeviceFilter();

            StatusText.Text = item.IsHidden
                ? $"{item.Name} hidden. Use SHOW HIDDEN at the top to bring it back."
                : $"{item.Name} is visible again.";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusSuccessBrush");
        }

        private void ShowHiddenButton_Click(object sender, RoutedEventArgs e)
        {
            appSettings.ShowHiddenDevices = !appSettings.ShowHiddenDevices;
            SaveAppSettings();
            ApplyDeviceFilter();
        }

        // ---- start with Windows ----------------------------------------------------

        private void RefreshHeaderToggles()
        {
            bool onStartup = StartupManager.IsEnabled();
            StartupButton.Content = onStartup ? "AUTOSTART: ON" : "AUTOSTART: OFF";
            IndependentVolumesButton.Content = appSettings.IndependentVolumes
                ? "INDEPENDENT VOLUMES: ON" : "INDEPENDENT VOLUMES: OFF";
            RemoteControlButton.Content = appSettings.AllowRemoteControl
                ? "REMOTE CONTROL: ON" : "REMOTE CONTROL: OFF";
            StartupButton.ToolTip = onStartup
                ? "SoundSync starts when you sign in to Windows and reconnects using these same settings. Click to turn off."
                : "Click to have Windows start SoundSync when you sign in and reconnect automatically with the devices ticked here.";
        }

        private void StartupButton_Click(object sender, RoutedEventArgs e)
        {
            bool turningOn = !StartupManager.IsEnabled();

            string problem = StartupManager.SetEnabled(turningOn);
            if (problem.Length > 0)
            {
                System.Windows.MessageBox.Show(problem, "Startup");
                RefreshHeaderToggles();
                return;
            }

            appSettings.AutoConnectOnLaunch = turningOn;
            SaveAppSettings();
            RefreshHeaderToggles();

            StatusText.Text = turningOn
                ? "SoundSync will start with Windows and reconnect automatically."
                : "SoundSync will no longer start with Windows.";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusSuccessBrush");
        }

        private async void CheckForUpdatesAsync()
        {
            try
            {
                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SoundSync-Client");

                string responseJson = await client.GetStringAsync("https://api.github.com/repos/sugumar247/SoundSync/releases/latest");
                
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("tag_name", out var tagProperty))
                {
                    string tag = tagProperty.GetString()?.Trim().TrimStart('v', 'V') ?? "";
                    if (Version.TryParse(tag, out var latestVersion))
                    {
                        var currentVersion = typeof(MainWindow).Assembly.GetName().Version;
                        if (currentVersion != null && latestVersion > currentVersion)
                        {
                            if (doc.RootElement.TryGetProperty("html_url", out var urlProperty))
                            {
                                latestReleaseUrl = urlProperty.GetString() ?? latestReleaseUrl;
                            }
                            
                            Dispatcher.Invoke(() =>
                            {
                                UpdateText.Text = $"A NEW UPDATE (v{tag}) IS AVAILABLE FOR SOUNDSYNC!";
                                UpdateBanner.Visibility = Visibility.Visible;
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Update check failed: {ex.Message}");
            }
        }

        private void UpdateDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = latestReleaseUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to open release URL: {ex.Message}");
            }
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.C)
            {
                ConnectButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.M)
            {
                ToggleMute();
                e.Handled = true;
            }
        }

        private void Slider_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (sender is not System.Windows.Controls.Slider slider) return;

            // Only adjust a slider the user has actually focused by clicking it.
            // Without this the wheel silently changes whichever slider the pointer
            // happens to be over while the user is just trying to scroll the device
            // list, which is how an output can end up muted at volume 0.
            if (!slider.IsKeyboardFocusWithin) return;

            double step;

            // Volume slider: 0 to 1  → 2% per tick
            if (slider.Minimum >= 0 && slider.Maximum <= 1)
                step = 0.02;
            // Delay slider: 0 to 500 → 1 ms per notch, so it can be dialled in exactly
            else if (slider.Maximum >= 100)
                step = 1;
            // EQ sliders: -12 to 12 → 0.5dB per tick
            else
                step = 0.5;

            slider.Value = Math.Clamp(
                slider.Value + (e.Delta > 0 ? step : -step),
                slider.Minimum,
                slider.Maximum);

            e.Handled = true; // prevent ListView from scrolling
        }

        private void ToggleMute()
        {
            if (!isConnected) return;

            isMuted = !isMuted;
            var items = DeviceListBox.ItemsSource as List<DeviceItem>;
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item.VolumeProvider != null)
                    {
                        item.VolumeProvider.Volume = isMuted ? 0f : item.Volume;
                    }
                    else
                    {
                        try
                        {
                            item.Device.AudioEndpointVolume.Mute = isMuted;
                        }
                        catch { }
                    }
                }
            }

            if (isMuted)
            {
                StatusText.Text = string.IsNullOrEmpty(currentStreamUrl) ? "Status: MUTED (Press M to Unmute)" : $"Streaming: {currentStreamUrl} | MUTED (Press M to Unmute)";
                StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusMutedBrush");
            }
            else
            {
                StatusText.Text = string.IsNullOrEmpty(currentStreamUrl) ? "Status: Connected and Routing Audio!" : $"Streaming: {currentStreamUrl} | Routing Audio!";
                StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusSuccessBrush");
            }
        }

        private void InitializeNotifyIcon()
        {
            notifyIcon = new Hardcodet.Wpf.TaskbarNotification.TaskbarIcon();
            notifyIcon.ToolTipText = "SoundSync";

            try
            {
                notifyIcon.IconSource = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.ico"));
            }
            catch { }

            notifyIcon.TrayMouseDoubleClick += (s, e) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            };

            var contextMenu = new System.Windows.Controls.ContextMenu();
            var openItem = new System.Windows.Controls.MenuItem { Header = "Open SoundSync" };
            openItem.Click += (s, e) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            };
            var resetItem = new System.Windows.Controls.MenuItem
            {
                Header = "Reset window size",
                ToolTip = "Forget the saved size and position, and fit the window to this monitor again. " +
                          "Useful after unplugging a screen, or if the window ended up somewhere awkward."
            };
            resetItem.Click += (s, e) => ResetWindowSize();

            var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
            exitItem.Click += (s, e) =>
            {
                System.Windows.Application.Current.Shutdown();
            };
            contextMenu.Items.Add(openItem);
            contextMenu.Items.Add(resetItem);
            contextMenu.Items.Add(new System.Windows.Controls.Separator());
            contextMenu.Items.Add(exitItem);

            notifyIcon.ContextMenu = contextMenu;
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && notifyIcon != null)
            {
                Hide();
                notifyIcon.ShowBalloonTip("SoundSync", "SoundSync is running in the system tray.", Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
            }
        }

        private void LoadDevices()
        {
            try
            {
                var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                var defaultDevice = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);

                allDevices = audioEngine.GetActiveRenderDevices();
                var items = allDevices.Select(d => {
                    var item = new DeviceItem
                    {
                        Device = d,
                        IsSelected = false,
                        // The VOLUME slider now drives the Windows endpoint volume directly,
                        // so the in-stream gain stays at unity. Keeping a second, invisible
                        // gain here is what let an output sit silently at zero.
                        Volume = 1.0f,
                        IsDefaultDevice = (d.ID == defaultDevice.ID),
                        DelayChangedCallback = () => Dispatcher.BeginInvoke(new Action(UpdateRelativeDelays)),
                        FormatChangedCallback = changed => Dispatcher.BeginInvoke(new Action(() => HandleFormatChanged(changed)))
                    };
                    item.AttachSystemVolume(action => Dispatcher.BeginInvoke(action));
                    item.LoadSystemFormat();
                    item.LoadConnectionKind();
                    return item;
                }).ToList();
                allItems = items;
                // Visibility is decided by ReloadDevices, after the profile is read.
                DeviceListBox.ItemsSource = items;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Error loading devices: " + ex.Message);
            }
        }

        private void SetDefaultDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button button) return;
            if (button.Tag is not DeviceItem item) return;

            if (item.IsDefaultDevice)
            {
                System.Windows.MessageBox.Show($"{item.Name} is already the system default output.");
                return;
            }

            // The engine captures from whatever the default device is, so a switch while
            // connected would leave it mirroring the old source. Stop first, then reconnect.
            bool wasConnected = audioEngine.IsConnected;
            if (wasConnected) Disconnect();

            if (!Services.SystemAudioConfig.SetAsDefault(item.Device))
            {
                System.Windows.MessageBox.Show($"Windows refused to make {item.Name} the default output.");
                return;
            }

            ReloadDevices();

            StatusText.Text = $"System default is now: {item.Name}" +
                              (wasConnected ? " - press CONNECT again to resume mirroring." : "");
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusSuccessBrush");
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (isConnected)
            {
                System.Windows.MessageBox.Show("Please DISCONNECT first before refreshing the device list.");
                return;
            }
            ReloadDevices();
        }

        private bool isLightTheme = false;

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyTheme(!isLightTheme);
        }

        private void ApplyTheme(bool isLight)
        {
            isLightTheme = isLight;
            var resources = this.Resources;

            if (isLight)
            {
                // Frontier Mode: Vintage weathered parchment paper style
                resources["WindowBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#ECE1D0"));
                resources["PanelBackgroundBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#E4D5BE"));
                resources["ChannelCardBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#F5ECE0"));
                resources["ConsoleBorderBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#9F8F75"));
                resources["TextForegroundBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#1B1612"));
                resources["TextSecondaryBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#4E3E33"));
                resources["TextMutedBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#7D6857"));
                resources["NoDevicesTextBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#C5B59C"));
                resources["ListBoxSeparatorBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#DCD0B9"));
                resources["ControlBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#F7F2E9"));
                resources["ControlBorderBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#BFB097"));
                resources["ThumbBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#8A1212"));
                resources["ThumbBorderBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#A80505"));
                resources["DelayTextBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#8A1212"));
                resources["StatusPanelBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#DFD0B7"));
                resources["ShortcutBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#E2D2B9"));
                resources["ShortcutKeyBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#CABAA2"));
                resources["ShortcutKeyShadowBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#A0917C"));
                resources["BadgeBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#F3E6E6"));
                resources["BadgeFgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#8A1212"));
                resources["StatusMutedBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#8A6012"));
                resources["StatusSuccessBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#2E5A27"));
                resources["StatusErrorBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#8A1212"));

                resources["AccentBlueBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#8A1212"));
                resources["WarningRedBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#8A1212"));

                // Connect Button Light Brushes
                resources["ConnectButtonBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#8A1212"));
                resources["ConnectButtonHoverBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#A80505"));
                resources["ConnectButtonBorderBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#3D1B1B"));
                resources["ConnectButtonConnectedBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#433E3B"));
                resources["ConnectButtonConnectedHoverBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#5C5551"));
                resources["ConnectButtonConnectedFgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#EADDC9"));
            }
            else
            {
                // Campfire Mode: Deep earthy dark charcoal/black gradient
                var darkBg = new System.Windows.Media.LinearGradientBrush();
                darkBg.StartPoint = new System.Windows.Point(0, 0);
                darkBg.EndPoint = new System.Windows.Point(0, 1);
                darkBg.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#161413"), 0.0));
                darkBg.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#0A0909"), 1.0));
                resources["WindowBgBrush"] = darkBg;

                resources["PanelBackgroundBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#1B1917"));
                resources["ChannelCardBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#24201E"));
                resources["ConsoleBorderBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#3B3530"));
                resources["TextForegroundBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#EADDC9"));
                resources["TextSecondaryBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#C2B59D"));
                resources["TextMutedBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#807361"));
                resources["NoDevicesTextBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#3D3732"));
                resources["ListBoxSeparatorBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#2E2824"));
                resources["ControlBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#0F0E0D"));
                resources["ControlBorderBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#423C37"));
                resources["ThumbBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#A80505"));
                resources["ThumbBorderBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#C10B0B"));
                resources["DelayTextBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#DCA462"));
                resources["StatusPanelBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#141211"));
                resources["ShortcutBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#1C1917"));
                resources["ShortcutKeyBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#2C2723"));
                resources["ShortcutKeyShadowBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#0A0909"));
                resources["BadgeBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#2D0B0B"));
                resources["BadgeFgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#FF3E3E"));
                resources["StatusMutedBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#C29F72"));
                resources["StatusSuccessBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#859F6C"));
                resources["StatusErrorBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#A80505"));

                resources["AccentBlueBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#A80505"));
                resources["WarningRedBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#C10B0B"));

                // Connect Button Dark Brushes
                resources["ConnectButtonBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#A80505"));
                resources["ConnectButtonHoverBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#C10B0B"));
                resources["ConnectButtonBorderBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#EADDC9"));
                resources["ConnectButtonConnectedBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#2D2723"));
                resources["ConnectButtonConnectedHoverBgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#3D3530"));
                resources["ConnectButtonConnectedFgBrush"] = new System.Windows.Media.SolidColorBrush(ColorFromHex("#EADDC9"));
            }

            if (isLight)
            {
                ThemeButtonText.Text = "CAMPFIRE MODE";
                ThemeIconPath.Data = System.Windows.Media.Geometry.Parse("M9,2C7.95,2 6.95,2.23 6.05,2.63C9,3.87 11,6.7 11,10C11,13.3 9,16.13 6.05,17.37C6.95,17.77 7.95,18 9,18A8,8 0 0,0 17,10A8,8 0 0,0 9,2Z");
                ThemeButton.Foreground = new System.Windows.Media.SolidColorBrush(ColorFromHex("#8A1212"));
            }
            else
            {
                ThemeButtonText.Text = "FRONTIER MODE";
                ThemeIconPath.Data = System.Windows.Media.Geometry.Parse("M12,7A5,5 0 0,0 7,12A5,5 0 0,0 12,17A5,5 0 0,0 17,12A5,5 0 0,0 12,7M12,2A1,1 0 0,1 13,3V5A1,1 0 0,1 12,6A1,1 0 0,1 11,5V3A1,1 0 0,1 12,2M12,18A1,1 0 0,1 13,19V21A1,1 0 0,1 12,22A1,1 0 0,1 11,21V19A1,1 0 0,1 12,18M2,12A1,1 0 0,1 3,11H5A1,1 0 0,1 6,12A1,1 0 0,1 5,13H3A1,1 0 0,1 2,12M18,12A1,1 0 0,1 19,11H21A1,1 0 0,1 22,12A1,1 0 0,1 21,13H19A1,1 0 0,1 18,12M5.63,4.22A1,1 0 0,1 7.05,4.22L8.46,5.64A1,1 0 0,1 8.46,7.05A1,1 0 0,1 7.05,7.05L5.63,5.64A1,1 0 0,1 5.63,4.22M15.54,14.14A1,1 0 0,1 16.95,14.14L18.36,15.56A1,1 0 0,1 18.36,16.97A1,1 0 0,1 16.95,16.97L15.54,15.56A1,1 0 0,1 15.54,14.14M18.36,4.22A1,1 0 0,1 18.36,5.64L16.95,7.05A1,1 0 0,1 15.54,7.05A1,1 0 0,1 15.54,5.64L16.95,4.22A1,1 0 0,1 18.36,4.22M8.46,14.14A1,1 0 0,1 8.46,16.97L7.05,18.36A1,1 0 0,1 5.63,18.36A1,1 0 0,1 5.63,16.97L7.05,14.14A1,1 0 0,1 8.46,14.14Z");
                ThemeButton.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("AccentBlueBrush");
            }
        }

        private System.Windows.Media.Color ColorFromHex(string hex)
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        }

        private void PresetButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as System.Windows.Controls.Button;
            if (button == null) return;

            var deviceItem = button.DataContext as DeviceItem;
            if (deviceItem == null) return;

            string? preset = button.Tag as string;
            if (preset == "Campfire")
            {
                deviceItem.Bass = 2f;
                deviceItem.Mid = 5f;
                deviceItem.Treble = 3f;
            }
            else if (preset == "Gunslinger")
            {
                deviceItem.Bass = 6f;
                deviceItem.Mid = -2f;
                deviceItem.Treble = 4f;
            }
            else if (preset == "Saloon")
            {
                deviceItem.Bass = -3f;
                deviceItem.Mid = 6f;
                deviceItem.Treble = -2f;
            }
            else if (preset == "Reset")
            {
                deviceItem.Bass = 0f;
                deviceItem.Mid = 0f;
                deviceItem.Treble = 0f;
            }
        }


        private void CheckBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (isConnected)
            {
                e.Handled = true;
            }
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (isConnected)
            {
                Disconnect();
            }
            else
            {
                Connect();
            }
        }

        private void Connect()
        {
            var items = DeviceListBox.ItemsSource as List<DeviceItem>;
            if (items == null) return;

            var selectedDevices = items.Where(i => i.IsSelected).ToList();
            if (selectedDevices.Count == 0)
            {
                System.Windows.MessageBox.Show("Please check at least one device to connect.");
                return;
            }

            try
            {
                string ip = GetLocalIPAddress();
                int port = 8090;
                linkServer = new NetworkStreamer();
                linkServer.ClientsChanged += () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshRemoteListeners();
                    PushDeviceSnapshot();
                    foreach (var c in linkServer?.GetClients() ?? new List<LinkClient>())
                        (linkServer as NetworkStreamer)?.SendListenerState(c);
                }));
                linkServer.CommandReceived += json => Dispatcher.BeginInvoke(new Action(() => HandleRemoteCommand(json)));

                audioEngine.Connect(selectedDevices, linkServer, log => { }, () =>
                {
                    Dispatcher.BeginInvoke(new Action(Disconnect));
                });

                StartDefaultDevicePeakTimer();
                StartLatencyTimer();
                StartAutoAlignTimer();
                RefreshMakeUpGain();

                isMuted = false;
                ConnectButton.Content = "DISCONNECT";
                ConnectButton.Tag = "Connected";

                ShowLinkPanel($"http://{ip}:{port}/?t={Services.LinkAuth.Token}");
                StatusText.Text = "Routing audio to the ticked outputs.";
                StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusSuccessBrush");

                SaveCurrentProfile();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Error starting audio routing: " + ex.Message);
                Disconnect();
            }
        }

        private void Disconnect()
        {
            var items = DeviceListBox.ItemsSource as List<DeviceItem> ?? new List<DeviceItem>();
            audioEngine.Disconnect(items);

            if (linkServer != null)
            {
                try
                {
                    linkServer.Stop();
                }
                catch { }
                linkServer = null;
            }

            StopDefaultDevicePeakTimer();

            isMuted = false;
            StopLatencyTimer();
            StopAutoAlignTimer();
            HideLinkPanel();
            RemotePanel.Visibility = Visibility.Collapsed;
            currentStreamUrl = string.Empty;
            ConnectButton.Content = "ACTIVATE SOUNDSYNC CONSOLE";
            ConnectButton.Tag = "Disconnected";
            StatusText.Text = "Status: Disconnected";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusErrorBrush");
        }

        public void UpdateRelativeDelays()
        {
            var items = DeviceListBox.ItemsSource as List<DeviceItem>;
            if (items == null) return;
            audioEngine.UpdateRelativeDelays(items);
        }

        private void StartDefaultDevicePeakTimer()
        {
            defaultDevicePeakTimer = new System.Windows.Threading.DispatcherTimer();
            defaultDevicePeakTimer.Interval = TimeSpan.FromMilliseconds(50);
            defaultDevicePeakTimer.Tick += (s, e) =>
            {
                if (!isConnected) return;
                try
                {
                    var defaultItem = allItems.FirstOrDefault(i => i.IsDefaultDevice);
                    if (defaultItem == null) return;

                    // Read the level from the captured stream, corrected for this device's
                    // volume, rather than from AudioMeterInformation. The Windows meter sits
                    // after the volume control, so its bar shrank as the volume came down and
                    // disagreed with every other row. This one shows the material itself.
                    defaultItem.PeakLevel = audioEngine.SourcePeakLevel;
                }
                catch { }
            };
            defaultDevicePeakTimer.Start();
        }

        private void StopDefaultDevicePeakTimer()
        {
            if (defaultDevicePeakTimer != null)
            {
                defaultDevicePeakTimer.Stop();
                defaultDevicePeakTimer = null;
            }
        }

        private string GetLocalIPAddress()
        {
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    IPEndPoint? endPoint = socket.LocalEndPoint as IPEndPoint;
                    return endPoint?.Address.ToString() ?? "127.0.0.1";
                }
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        private void SaveCurrentProfile()
        {
            try
            {
                var items = allItems;
                if (items.Count == 0) return;

                var data = items.Select(i => new SavedDeviceSettings
                {
                    DeviceId = i.Device.ID,
                    IsSelected = i.IsSelected,
                    Volume = i.Volume,
                    Delay = i.Delay,
                    Bass = i.Bass,
                    Mid = i.Mid,
                    Treble = i.Treble,
                    IsHidden = i.IsHidden,
                    AutoAlignDelay = i.AutoAlignDelay,
                    SyncVolumeWithDefault = i.SyncVolumeWithDefault,
                    VolumeRatioToDefault = i.VolumeRatioToDefault
                }).ToList();

                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(profilePath, json);
            }
            catch
            {
                // Silently ignore saving errors
            }
        }

        private void LoadSavedProfile()
        {
            try
            {
                if (!File.Exists(profilePath)) return;

                var items = allItems;
                if (items.Count == 0) return;

                string json = File.ReadAllText(profilePath);
                var savedData = JsonSerializer.Deserialize<List<SavedDeviceSettings>>(json);
                if (savedData == null) return;

                foreach (var saved in savedData)
                {
                    var matchingItem = items.FirstOrDefault(i => i.Device.ID == saved.DeviceId);
                    if (matchingItem != null)
                    {
                        matchingItem.IsSelected = saved.IsSelected;
                        // saved.Volume is deliberately not restored: the VOLUME slider is
                        // the Windows endpoint volume now, which Windows already persists.
                        matchingItem.Delay = saved.Delay;
                        matchingItem.Bass = saved.Bass;
                        matchingItem.Mid = saved.Mid;
                        matchingItem.Treble = saved.Treble;
                        matchingItem.IsHidden = saved.IsHidden;
                        matchingItem.AutoAlignDelay = saved.AutoAlignDelay;
                        matchingItem.SyncVolumeWithDefault = saved.SyncVolumeWithDefault;
                        matchingItem.VolumeRatioToDefault = saved.VolumeRatioToDefault <= 0 ? 1.0f : saved.VolumeRatioToDefault;
                    }
                }
            }
            catch
            {
                // Silently ignore loading errors
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveCurrentProfile();
            Disconnect();

            if (notifyIcon != null)
            {
                notifyIcon.Visibility = Visibility.Collapsed;
                notifyIcon.Dispose();
            }
            base.OnClosed(e);
        }
    }
}
