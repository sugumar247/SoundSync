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
            this.Loaded += MainWindow_Loaded;

            ApplyTheme(false);
        }

        private string latestReleaseUrl = "https://github.com/sugumar247/SoundSync/releases";

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSavedProfile();
            CheckForUpdatesAsync();
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

            double step;

            // Volume slider: 0 to 1  → 2% per tick
            if (slider.Minimum >= 0 && slider.Maximum <= 1)
                step = 0.02;
            // Delay slider: -3000 to 3000 → 10ms per tick
            else if (slider.Maximum >= 100)
                step = 10;
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
            var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
            exitItem.Click += (s, e) =>
            {
                System.Windows.Application.Current.Shutdown();
            };
            contextMenu.Items.Add(openItem);
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
                    float initialVol = 1.0f;
                    try
                    {
                        initialVol = d.AudioEndpointVolume.MasterVolumeLevelScalar;
                    }
                    catch { }
                    return new DeviceItem
                    {
                        Device = d,
                        IsSelected = false,
                        Volume = initialVol,
                        IsDefaultDevice = (d.ID == defaultDevice.ID),
                        DelayChangedCallback = () => Dispatcher.BeginInvoke(new Action(UpdateRelativeDelays))
                    };
                }).ToList();
                DeviceListBox.ItemsSource = items;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Error loading devices: " + ex.Message);
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (isConnected)
            {
                System.Windows.MessageBox.Show("Please DISCONNECT first before refreshing the device list.");
                return;
            }
            LoadDevices();
            LoadSavedProfile();
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

                audioEngine.Connect(selectedDevices, linkServer, log => { }, () =>
                {
                    Dispatcher.BeginInvoke(new Action(Disconnect));
                });

                StartDefaultDevicePeakTimer();

                isMuted = false;
                ConnectButton.Content = "DISCONNECT";
                ConnectButton.Tag = "Connected";

                currentStreamUrl = $"http://{ip}:{port}";
                StatusText.Text = $"Streaming: {currentStreamUrl} | Routing Audio!";
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
                    if (enumerator == null) enumerator = new MMDeviceEnumerator();
                    var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    var items = DeviceListBox.ItemsSource as List<DeviceItem>;
                    if (items != null)
                    {
                        var defaultItem = items.FirstOrDefault(i => i.Device.ID == defaultDevice.ID);
                        if (defaultItem != null)
                        {
                            // Query peak level from Windows AudioMeterInformation
                            defaultItem.PeakLevel = defaultDevice.AudioMeterInformation.MasterPeakValue;
                        }
                    }
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
                var items = DeviceListBox.ItemsSource as List<DeviceItem>;
                if (items == null) return;

                var data = items.Select(i => new SavedDeviceSettings
                {
                    DeviceId = i.Device.ID,
                    IsSelected = i.IsSelected,
                    Volume = i.Volume,
                    Delay = i.Delay,
                    Bass = i.Bass,
                    Mid = i.Mid,
                    Treble = i.Treble
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

                var items = DeviceListBox.ItemsSource as List<DeviceItem>;
                if (items == null) return;

                string json = File.ReadAllText(profilePath);
                var savedData = JsonSerializer.Deserialize<List<SavedDeviceSettings>>(json);
                if (savedData == null) return;

                foreach (var saved in savedData)
                {
                    var matchingItem = items.FirstOrDefault(i => i.Device.ID == saved.DeviceId);
                    if (matchingItem != null)
                    {
                        matchingItem.IsSelected = saved.IsSelected;
                        matchingItem.Volume = saved.Volume;
                        matchingItem.Delay = saved.Delay;
                        matchingItem.Bass = saved.Bass;
                        matchingItem.Mid = saved.Mid;
                        matchingItem.Treble = saved.Treble;
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