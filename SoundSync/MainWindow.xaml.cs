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

namespace SoundSync
{
    public partial class MainWindow : Window
    {
        private MMDeviceEnumerator? enumerator;
        private List<MMDevice>? allDevices;

        // NAudio Audio Components
        private WasapiLoopbackCapture? loopbackCapture;
        private readonly List<WasapiOut> outputStreams = new List<WasapiOut>();
        private readonly List<BufferedWaveProvider> buffers = new List<BufferedWaveProvider>();
        private bool isConnected = false;
        private bool isMuted = false;

        // SoundSync Link Wi-Fi Stream Server
        private SoundSyncLinkServer? linkServer;
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
                enumerator = new MMDeviceEnumerator();
                allDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
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
                        Volume = initialVol
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
                if (enumerator == null) enumerator = new MMDeviceEnumerator();
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

                    // 1. Convert raw bytes to float Samples
                    var sampleProvider = buffer.ToSampleProvider();

                    // 2. Attach volume control
                    var volumeProvider = new VolumeSampleProvider(sampleProvider)
                    {
                        Volume = deviceItem.Volume
                    };
                    deviceItem.VolumeProvider = volumeProvider;

                    // 3. Attach 3-band Equalizer control
                    var equalizerProvider = new EqualizerSampleProvider(volumeProvider)
                    {
                        BassDb = deviceItem.Bass,
                        MidDb = deviceItem.Mid,
                        TrebleDb = deviceItem.Treble
                    };
                    deviceItem.EqualizerProvider = equalizerProvider;

                    // 4. Attach custom dynamic audio delay
                    var delayProvider = new DelaySampleProvider(equalizerProvider)
                    {
                        DelayMilliseconds = 0
                    };
                    deviceItem.DelayProvider = delayProvider;

                    // 5. Attach custom peak level VU meter
                    var meterProvider = new MeteringSampleProvider(delayProvider, (peak) =>
                    {
                        deviceItem.PeakLevel = peak;
                    });

                    // 6. Convert back to wave and initialize wasapiOut
                    wasapiOut.Init(meterProvider.ToWaveProvider());

                    wasapiOut.Play();
                    outputStreams.Add(wasapiOut);
                    buffers.Add(buffer);
                }

                if (outputStreams.Count == 0 && !selectedDevices.Any(d => d.Device.ID == defaultDevice.ID))
                {
                    System.Windows.MessageBox.Show("Please select at least one active device (either default or secondary).");
                    Disconnect();
                    return;
                }

                // Calculate and apply initial relative delays
                UpdateRelativeDelays();

                // START ZERO-CONFIGURATION WIRELESS TCP/WEBSOCKET STREAM SERVER
                string ip = GetLocalIPAddress();
                int port = 8090;
                linkServer = new SoundSyncLinkServer(captureFormat.SampleRate);
                linkServer.Start(port);

                loopbackCapture.DataAvailable += (s, args) =>
                {
                    // Broadcast directly over local Wi-Fi link to connected browser clients
                    linkServer?.BroadcastAudio(args.Buffer, args.BytesRecorded);

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

                StartDefaultDevicePeakTimer();

                isConnected = true;
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
            if (loopbackCapture != null)
            {
                loopbackCapture.StopRecording();
                loopbackCapture.Dispose();
                loopbackCapture = null;
            }

            foreach (var stream in outputStreams)
            {
                try
                {
                    stream.Stop();
                    stream.Dispose();
                }
                catch { }
            }

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

            var items = DeviceListBox.ItemsSource as List<DeviceItem>;
            if (items != null)
            {
                foreach (var item in items)
                {
                    item.VolumeProvider = null;
                    item.EqualizerProvider = null;
                    item.DelayProvider = null;
                    item.PeakLevel = 0f;
                }
            }

            outputStreams.Clear();
            buffers.Clear();

            isConnected = false;
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

            var activeItems = items.Where(i => i.IsSelected && i.DelayProvider != null).ToList();
            if (activeItems.Count == 0) return;

            int minDelaySetting = activeItems.Min(i => i.Delay);

            foreach (var item in activeItems)
            {
                if (item.DelayProvider != null)
                {
                    item.DelayProvider.DelayMilliseconds = item.Delay - minDelaySetting;
                }
            }
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

    public class SavedDeviceSettings
    {
        public string DeviceId { get; set; } = string.Empty;
        public bool IsSelected { get; set; }
        public float Volume { get; set; }
        public int Delay { get; set; }
        public float Bass { get; set; }
        public float Mid { get; set; }
        public float Treble { get; set; }
    }

    public class DeviceItem : INotifyPropertyChanged
    {
        public MMDevice Device { get; set; } = null!;
        public string Name => Device.FriendlyName;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        private float _volume = 1.0f;
        public float Volume
        {
            get => _volume;
            set
            {
                _volume = value;
                OnPropertyChanged();

                if (VolumeProvider != null)
                {
                    VolumeProvider.Volume = _volume;
                }
            }
        }

        private int _delay = 0;
        public int Delay
        {
            get => _delay;
            set
            {
                _delay = value;
                OnPropertyChanged();

                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => {
                    var mainWin = System.Windows.Application.Current.MainWindow as MainWindow;
                    mainWin?.UpdateRelativeDelays();
                }));
            }
        }

        private float _bass = 0f;
        public float Bass
        {
            get => _bass;
            set
            {
                _bass = value;
                OnPropertyChanged();
                if (EqualizerProvider != null)
                {
                    EqualizerProvider.BassDb = _bass;
                }
            }
        }

        private float _mid = 0f;
        public float Mid
        {
            get => _mid;
            set
            {
                _mid = value;
                OnPropertyChanged();
                if (EqualizerProvider != null)
                {
                    EqualizerProvider.MidDb = _mid;
                }
            }
        }

        private float _treble = 0f;
        public float Treble
        {
            get => _treble;
            set
            {
                _treble = value;
                OnPropertyChanged();
                if (EqualizerProvider != null)
                {
                    EqualizerProvider.TrebleDb = _treble;
                }
            }
        }

        private float _peakLevel = 0f;
        public float PeakLevel
        {
            get => _peakLevel;
            set
            {
                _peakLevel = value;
                OnPropertyChanged();
            }
        }

        public VolumeSampleProvider? VolumeProvider { get; set; }
        public EqualizerSampleProvider? EqualizerProvider { get; set; }
        public DelaySampleProvider? DelayProvider { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class EqualizerSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly BiQuadFilter[] filters;
        private float bassDb;
        private float midDb;
        private float trebleDb;
        private readonly object lockObject = new object();
        private bool filtersNeedUpdate = true;

        public EqualizerSampleProvider(ISampleProvider source)
        {
            this.source = source;
            filters = new BiQuadFilter[source.WaveFormat.Channels * 3];
            CreateFilters();
        }

        public WaveFormat WaveFormat => source.WaveFormat;

        public float BassDb
        {
            get => bassDb;
            set { lock (lockObject) { bassDb = value; filtersNeedUpdate = true; } }
        }

        public float MidDb
        {
            get => midDb;
            set { lock (lockObject) { midDb = value; filtersNeedUpdate = true; } }
        }

        public float TrebleDb
        {
            get => trebleDb;
            set { lock (lockObject) { trebleDb = value; filtersNeedUpdate = true; } }
        }

        private void CreateFilters()
        {
            int channels = WaveFormat.Channels;
            int sampleRate = WaveFormat.SampleRate;

            for (int c = 0; c < channels; c++)
            {
                filters[c * 3] = BiQuadFilter.LowShelf(sampleRate, 200, 1.0f, bassDb);
                filters[c * 3 + 1] = BiQuadFilter.PeakingEQ(sampleRate, 1000, 1.0f, midDb);
                filters[c * 3 + 2] = BiQuadFilter.HighShelf(sampleRate, 5000, 1.0f, trebleDb);
            }
            filtersNeedUpdate = false;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            lock (lockObject)
            {
                if (filtersNeedUpdate)
                {
                    CreateFilters();
                }

                int channels = WaveFormat.Channels;
                for (int i = 0; i < read; i++)
                {
                    int channel = i % channels;
                    float sample = buffer[offset + i];

                    sample = filters[channel * 3].Transform(sample);
                    sample = filters[channel * 3 + 1].Transform(sample);
                    sample = filters[channel * 3 + 2].Transform(sample);

                    buffer[offset + i] = sample;
                }
            }
            return read;
        }
    }

    public class DelaySampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly Queue<float> delayQueue = new Queue<float>();
        private readonly object lockObject = new object();
        private int delaySamples;
        private int currentDelayMs;

        public DelaySampleProvider(ISampleProvider source)
        {
            this.source = source;
        }

        public WaveFormat WaveFormat => source.WaveFormat;

        public int DelayMilliseconds
        {
            get => currentDelayMs;
            set
            {
                lock (lockObject)
                {
                    currentDelayMs = value;
                    delaySamples = (WaveFormat.SampleRate * WaveFormat.Channels * value) / 1000;

                    // Trim down items if the delay size decreases
                    while (delayQueue.Count > delaySamples)
                    {
                        delayQueue.Dequeue();
                    }
                }
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            // Read input samples from the preceding pipeline source first
            int read = source.Read(buffer, offset, count);

            lock (lockObject)
            {
                if (delaySamples <= 0)
                {
                    delayQueue.Clear();
                    return read;
                }

                for (int i = 0; i < read; i++)
                {
                    float incomingSample = buffer[offset + i];

                    // Populate queue with zero silence until the requested millisecond delay distance is filled
                    if (delayQueue.Count < delaySamples)
                    {
                        delayQueue.Enqueue(incomingSample);
                        buffer[offset + i] = 0f; // Return silence during initial delay loading phase
                    }
                    else
                    {
                        // Extract oldest sample and inject newest sample simultaneously
                        buffer[offset + i] = delayQueue.Dequeue();
                        delayQueue.Enqueue(incomingSample);
                    }
                }

                return read;
            }
        }
    }

    public class MeteringSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly Action<float> peakCallback;
        private float currentPeak = 0f;

        public MeteringSampleProvider(ISampleProvider source, Action<float> peakCallback)
        {
            this.source = source;
            this.peakCallback = peakCallback;
        }

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = source.Read(buffer, offset, count);
            float maxVal = 0f;

            for (int i = 0; i < samplesRead; i++)
            {
                float abs = Math.Abs(buffer[offset + i]);
                if (maxVal > abs) { } // dummy instruction to silence warnings
                if (abs > maxVal) maxVal = abs;
            }

            if (maxVal > currentPeak)
            {
                currentPeak = maxVal;
            }
            else
            {
                currentPeak = currentPeak * 0.95f + maxVal * 0.05f;
            }

            peakCallback(currentPeak);
            return samplesRead;
        }
    }

    // Zero-Configuration, Drop-In SoundSync Link TCP / WebSocket Server
    public class SoundSyncLinkServer
    {
        private TcpListener? tcpListener;
        private readonly List<WebSocket> clients = new List<WebSocket>();
        private readonly object clientLock = new object();
        private bool isRunning = false;
        private readonly string htmlPage;
        private CancellationTokenSource? cts;

        public SoundSyncLinkServer(int sampleRate)
        {
            htmlPage = GetHtmlTemplate().Replace("{{SAMPLE_RATE}}", sampleRate.ToString());
        }

        public void Start(int port)
        {
            tcpListener = new TcpListener(IPAddress.Any, port);

            try
            {
                tcpListener.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to bind streaming network target on port {port}.", ex);
            }

            isRunning = true;
            cts = new CancellationTokenSource();

            // Listen for connections asynchronously inside background loops
            Task.Run(() => AcceptConnectionsAsync(cts.Token));
        }

        private async Task AcceptConnectionsAsync(CancellationToken token)
        {
            while (isRunning && tcpListener != null && !token.IsCancellationRequested)
            {
                try
                {
                    TcpClient tcpClient = await tcpListener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClientAsync(tcpClient, token), token);
                }
                catch
                {
                    break;
                }
            }
        }

        private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken token)
        {
            using var stream = tcpClient.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);

            try
            {
                // Read input HTTP context data blocks
                List<string> headers = new List<string>();
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    headers.Add(line);
                }

                if (headers.Count == 0) return;

                bool isWebSocket = headers.Any(h => h.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase));

                if (isWebSocket)
                {
                    // Compute handshake signatures using SHA-1 keys to establish connection upgrades
                    string secKeyLine = headers.First(h => h.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
                    string secKey = secKeyLine.Split(':')[1].Trim();
                    string acceptKey = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(secKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

                    var handshake = "HTTP/1.1 101 Switching Protocols\r\n" +
                                    "Upgrade: websocket\r\n" +
                                    "Connection: Upgrade\r\n" +
                                    $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";

                    byte[] handshakeBytes = Encoding.UTF8.GetBytes(handshake);
                    await stream.WriteAsync(handshakeBytes, 0, handshakeBytes.Length, token);

                    WebSocket webSocket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));

                    lock (clientLock)
                    {
                        clients.Add(webSocket);
                    }

                    byte[] receiveBuffer = new byte[1024];
                    while (webSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
                    {
                        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }
                    }

                    lock (clientLock)
                    {
                        clients.Remove(webSocket);
                    }
                    try { await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); } catch { }
                }
                else
                {
                    // Regular HTTP response layout: return UI template frames directly
                    byte[] bodyBytes = Encoding.UTF8.GetBytes(htmlPage);
                    string httpResponse = "HTTP/1.1 200 OK\r\n" +
                                          "Content-Type: text/html; charset=utf-8\r\n" +
                                          $"Content-Length: {bodyBytes.Length}\r\n" +
                                          "Connection: close\r\n\r\n";

                    byte[] responseHeaderBytes = Encoding.UTF8.GetBytes(httpResponse);
                    await stream.WriteAsync(responseHeaderBytes, 0, responseHeaderBytes.Length, token);
                    await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, token);
                }
            }
            catch
            {
                // Disconnected or timed out
            }
            finally
            {
                tcpClient.Close();
            }
        }

        public void BroadcastAudio(byte[] buffer, int count)
        {
            if (clients.Count == 0 || !isRunning) return;

            lock (clientLock)
            {
                for (int i = clients.Count - 1; i >= 0; i--)
                {
                    var client = clients[i];
                    if (client.State == WebSocketState.Open)
                    {
                        // Process async transfers to decouple operations from the primary loopback loop
                        _ = SendAudioAsync(client, buffer, count);
                    }
                    else
                    {
                        clients.RemoveAt(i);
                        try { client.Dispose(); } catch { }
                    }
                }
            }
        }

        private async Task SendAudioAsync(WebSocket client, byte[] buffer, int count)
        {
            try
            {
                // Convert internal raw 16-bit PCM or IEEE float arrays directly into float views for Javascript layout interfaces
                int sampleCount = count / 4;
                float[] floatBuffer = new float[sampleCount];
                Buffer.BlockCopy(buffer, 0, floatBuffer, 0, count);

                byte[] binaryPayload = new byte[floatBuffer.Length * 4];
                Buffer.BlockCopy(floatBuffer, 0, binaryPayload, 0, binaryPayload.Length);

                await client.SendAsync(new ArraySegment<byte>(binaryPayload), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            catch
            {
                // Client dropped cleanly handled during loop iterations
            }
        }

        public void Stop()
        {
            isRunning = false;
            cts?.Cancel();

            lock (clientLock)
            {
                foreach (var client in clients)
                {
                    try { client.Dispose(); } catch { }
                }
                clients.Clear();
            }

            try
            {
                tcpListener?.Stop();
            }
            catch { }
            tcpListener = null;
        }

        private string GetHtmlTemplate()
        {
            return @"<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <title>SoundSync Link - Outlaw Edition</title>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <link href='https://fonts.googleapis.com/css2?family=Georgia:ital,wght@0,400;0,700;1,400&family=JetBrains+Mono:wght@400;700&display=swap' rel='stylesheet'>
    <style>
        @font-face {
            font-family: 'Chinese Rocks Rg';
            src: local('Chinese Rocks Rg'), local('ChineseRocks-Regular');
        }

        :root {
            --bg: #0C0A0A;
            --panel-bg: #1A1614;
            --border: #EADDC9;
            --accent: #A80505;
            --accent-hover: #C10B0B;
            --success: #DCA462;
            --error: #A80505;
            --text-primary: #EADDC9;
            --text-secondary: #8C7869;
        }

        * {
            box-sizing: border-box;
            margin: 0;
            padding: 0;
            user-select: none;
        }

        body {
            background-color: var(--bg);
            background-image: 
                radial-gradient(circle at 50% 50%, rgba(20, 15, 12, 0.8) 0%, rgba(10, 8, 8, 0.95) 100%),
                repeating-linear-gradient(0deg, rgba(0,0,0,0.08) 0px, rgba(0,0,0,0.08) 1px, transparent 1px, transparent 2px);
            color: var(--text-primary);
            font-family: 'Georgia', serif;
            display: flex;
            align-items: center;
            justify-content: center;
            min-height: 100vh;
            overflow: hidden;
            position: relative;
        }

        /* Ambient Red Glow in Background */
        .ambient-glow {
            position: absolute;
            width: 700px;
            height: 700px;
            background: radial-gradient(circle, rgba(168, 5, 5, 0.08) 0%, rgba(0, 0, 0, 0) 70%);
            top: 50%;
            left: 50%;
            transform: translate(-50%, -50%);
            z-index: 1;
            pointer-events: none;
            transition: all 0.8s ease;
        }

        .ambient-glow.active {
            background: radial-gradient(circle, rgba(220, 164, 98, 0.08) 0%, rgba(0, 0, 0, 0) 70%);
            width: 900px;
            height: 900px;
        }

        /* Outlaw Themed Box */
        .glass-card {
            position: relative;
            z-index: 10;
            width: 90%;
            max-width: 480px;
            background: var(--panel-bg);
            border: 3px solid var(--border);
            border-radius: 0px;
            padding: 40px;
            box-shadow: 8px 8px 0px #000;
            text-align: center;
            overflow: hidden;
        }

        .header-logo {
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 14px;
            margin-bottom: 28px;
        }

        .logo-icon {
            width: 42px;
            height: 42px;
            border-radius: 0;
            background: var(--accent);
            border: 2px solid var(--border);
            display: flex;
            align-items: center;
            justify-content: center;
            box-shadow: 3px 3px 0px #000;
            transition: transform 0.2s ease;
        }

        .logo-text {
            font-family: 'Chinese Rocks Rg', 'Georgia', serif;
            font-size: 32px;
            text-transform: uppercase;
            letter-spacing: 1px;
            color: var(--border);
            text-shadow: 2px 2px 0px #000;
        }

        /* Status HUD Group */
        .status-hud {
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 16px;
            margin: 24px 0;
            background: rgba(0, 0, 0, 0.2);
            border: 2px solid var(--border);
            border-radius: 0px;
            padding: 16px;
            box-shadow: inset 3px 3px 6px rgba(0,0,0,0.4);
        }

        .hud-item {
            text-align: left;
        }

        .hud-label {
            font-family: 'Chinese Rocks Rg', 'Georgia', serif;
            font-size: 14px;
            text-transform: uppercase;
            letter-spacing: 1px;
            color: var(--text-secondary);
            margin-bottom: 4px;
        }

        .hud-value {
            font-family: 'JetBrains Mono', monospace;
            font-size: 16px;
            font-weight: 700;
            color: var(--text-primary);
        }

        /* Visualizer Area */
        .visualizer-container {
            position: relative;
            width: 100%;
            height: 140px;
            background: #15110F;
            border: 2px solid var(--border);
            border-radius: 0px;
            margin: 28px 0;
            overflow: hidden;
            display: flex;
            align-items: center;
            justify-content: center;
            box-shadow: inset 4px 4px 8px rgba(0,0,0,0.5);
        }

        #visualizerCanvas {
            position: absolute;
            top: 0;
            left: 0;
            width: 100%;
            height: 100%;
            pointer-events: none;
            opacity: 0.15;
            transition: opacity 0.5s ease;
        }

        .visualizer-container.active #visualizerCanvas {
            opacity: 1;
        }

        .visualizer-placeholder {
            color: var(--text-secondary);
            font-family: 'Chinese Rocks Rg', 'Georgia', serif;
            font-size: 16px;
            letter-spacing: 1px;
            text-transform: uppercase;
            z-index: 2;
            transition: opacity 0.3s ease;
            display: flex;
            flex-direction: column;
            align-items: center;
            gap: 12px;
        }

        .visualizer-placeholder svg {
            width: 36px;
            height: 36px;
            stroke: var(--accent);
            fill: none;
            stroke-width: 2;
        }

        /* Primary Action Button */
        .btn-connect {
            position: relative;
            background: var(--accent);
            color: var(--text-primary);
            border: 2px solid var(--border);
            width: 100%;
            padding: 18px;
            font-family: 'Chinese Rocks Rg', 'Georgia', serif;
            font-size: 20px;
            text-transform: uppercase;
            letter-spacing: 1.5px;
            border-radius: 0px;
            cursor: pointer;
            transition: all 0.1s ease;
            box-shadow: 4px 4px 0px #000;
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 10px;
        }

        .btn-connect:hover {
            background: var(--accent-hover);
            transform: translate(-1px, -1px);
            box-shadow: 5px 5px 0px #000;
        }

        .btn-connect:active {
            transform: translate(2px, 2px);
            box-shadow: 2px 2px 0px #000;
        }

        .btn-connect.active {
            background: #2D2723;
            color: var(--success);
            border-color: var(--success);
            box-shadow: 4px 4px 0px #000;
        }

        .ripple-effect {
            position: absolute;
            background: rgba(234, 221, 201, 0.2);
            transform: scale(0);
            animation: ripple 0.4s linear;
            pointer-events: none;
        }

        @keyframes ripple {
            to {
                transform: scale(4);
                opacity: 0;
            }
        }
    </style>
</head>
<body>
    <div class='ambient-glow' id='bgGlow'></div>

    <div class='glass-card'>
        <div class='header-logo'>
            <div class='logo-icon'>
                <svg width='20' height='20' viewBox='0 0 24 24' fill='none' stroke='#EADDC9' stroke-width='2.5' stroke-linecap='round' stroke-linejoin='round'>
                    <path d='M3 18h18M6 18c0-3.5 2.5-6 6-6s6 2.5 6 6M8 12c0-2 1.5-4 4-4s4 2 4 4'></path>
                </svg>
            </div>
            <div class='logo-text'>SoundSync Link</div>
        </div>

        <div class='status-hud'>
            <div class='hud-item'>
                <div class='hud-label'>Connection</div>
                <div class='hud-value' id='statusVal'>IDLE</div>
            </div>
            <div class='hud-item'>
                <div class='hud-label'>Sample Rate</div>
                <div class='hud-value'>{{SAMPLE_RATE}} Hz</div>
            </div>
        </div>

        <div class='visualizer-container' id='visualizerBox'>
            <canvas id='visualizerCanvas'></canvas>
            <div class='visualizer-placeholder' id='visPlaceholder'>
                <svg viewBox='0 0 24 24'>
                    <path d='M12 2v20M17 5v14M22 9v6M7 5v14M2 9v6' stroke-linecap='round'></path>
                </svg>
                <span>Tap Connect to play stream</span>
            </div>
        </div>

        <button class='btn-connect' id='playBtn'>
            <span id='btnText'>CONNECT RECEIVER</span>
        </button>
    </div>

    <script>
        const playBtn = document.getElementById('playBtn');
        const btnText = document.getElementById('btnText');
        const statusVal = document.getElementById('statusVal');
        const visualizerBox = document.getElementById('visualizerBox');
        const visPlaceholder = document.getElementById('visPlaceholder');
        const bgGlow = document.getElementById('bgGlow');
        const canvas = document.getElementById('visualizerCanvas');
        const ctx = canvas.getContext('2d');

        let audioCtx = null;
        let ws = null;
        let startTime = 0;
        let analyser = null;
        let dataArray = null;

        // Resize Canvas dynamically
        function resizeCanvas() {
            canvas.width = visualizerBox.clientWidth * window.devicePixelRatio;
            canvas.height = visualizerBox.clientHeight * window.devicePixelRatio;
            ctx.scale(window.devicePixelRatio, window.devicePixelRatio);
        }
        window.addEventListener('resize', resizeCanvas);
        resizeCanvas();

        // Waveform Visualizer
        function drawVisualizer() {
            if (!analyser) return;
            requestAnimationFrame(drawVisualizer);

            analyser.getByteTimeDomainData(dataArray);

            const width = canvas.width / window.devicePixelRatio;
            const height = canvas.height / window.devicePixelRatio;

            ctx.clearRect(0, 0, width, height);
            
            // Draw visualizer glowing grid background
            ctx.strokeStyle = 'rgba(234, 221, 201, 0.02)';
            ctx.lineWidth = 1;
            for (let i = 20; i < width; i += 20) {
                ctx.beginPath();
                ctx.moveTo(i, 0);
                ctx.lineTo(i, height);
                ctx.stroke();
            }

            // Draw sound waveform
            ctx.lineWidth = 3;
            const grad = ctx.createLinearGradient(0, 0, width, 0);
            grad.addColorStop(0, '#A80505');
            grad.addColorStop(0.5, '#DCA462');
            grad.addColorStop(1, '#A80505');
            ctx.strokeStyle = grad;
            ctx.shadowBlur = 0;

            ctx.beginPath();

            const sliceWidth = width / dataArray.length;
            let x = 0;

            for (let i = 0; i < dataArray.length; i++) {
                const v = dataArray[i] / 128.0;
                const y = (v * height) / 2;

                if (i === 0) {
                    ctx.moveTo(x, y);
                } else {
                    ctx.lineTo(x, y);
                }

                x += sliceWidth;
            }

            ctx.lineTo(width, height / 2);
            ctx.stroke();
        }

        // Ripple Effect Generator
        playBtn.addEventListener('click', function(e) {
            const ripple = document.createElement('span');
            ripple.classList.add('ripple-effect');
            this.appendChild(ripple);
            
            const rect = this.getBoundingClientRect();
            const size = Math.max(rect.width, rect.height);
            ripple.style.width = ripple.style.height = `${size}px`;
            
            const x = e.clientX - rect.left - size / 2;
            const y = e.clientY - rect.top - size / 2;
            ripple.style.left = `${x}px`;
            ripple.style.top = `${y}px`;
            
            setTimeout(() => ripple.remove(), 600);
        });

        playBtn.onclick = () => {
            if (audioCtx) return;

            // Trigger Audio System
            audioCtx = new (window.AudioContext || window.webkitAudioContext)();
            analyser = audioCtx.createAnalyser();
            analyser.fftSize = 256;
            dataArray = new Uint8Array(analyser.frequencyBinCount);
            
            analyser.connect(audioCtx.destination);
            
            btnText.innerText = 'ESTABLISHING...';
            playBtn.style.background = '';
            statusVal.innerText = 'NEGOTIATING';

            const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
            ws = new WebSocket(`${protocol}//${window.location.host}`);
            ws.binaryType = 'arraybuffer';

            ws.onopen = () => {
                btnText.innerText = 'STREAM ACTIVE';
                playBtn.classList.add('active');
                playBtn.style.background = '';
                statusVal.innerText = 'CONNECTED';
                statusVal.style.color = 'var(--success)';
                visualizerBox.classList.add('active');
                visPlaceholder.style.opacity = '0';
                bgGlow.classList.add('active');
                setTimeout(() => visPlaceholder.style.display = 'none', 300);

                // Run visualizer loop
                drawVisualizer();
            };

            ws.onmessage = async (event) => {
                if (audioCtx.state === 'suspended') {
                    audioCtx.resume();
                }

                const arrayBuffer = event.data;
                const floatData = new Float32Array(arrayBuffer);
                
                const channels = 2; 
                const sampleCount = floatData.length / channels;
                
                const audioBuffer = audioCtx.createBuffer(channels, sampleCount, {{SAMPLE_RATE}});
                
                for (let channel = 0; channel < channels; channel++) {
                    const nowBuffering = audioBuffer.getChannelData(channel);
                    for (let i = 0; i < sampleCount; i++) {
                        nowBuffering[i] = floatData[i * channels + channel];
                    }
                }

                const bufferSource = audioCtx.createBufferSource();
                bufferSource.buffer = audioBuffer;
                
                // Route stream through Analyzer Node for live waveforms
                bufferSource.connect(analyser);

                if (startTime < audioCtx.currentTime) {
                    startTime = audioCtx.currentTime + 0.05; 
                }
                bufferSource.start(startTime);
                startTime += audioBuffer.duration;
            };

            ws.onclose = () => {
                btnText.innerText = 'DISCONNECTED';
                playBtn.classList.remove('active');
                playBtn.style.background = 'var(--error)';
                statusVal.innerText = 'CLOSED';
                statusVal.style.color = 'var(--error)';
                visualizerBox.classList.remove('active');
                bgGlow.classList.remove('active');
                visPlaceholder.style.display = 'flex';
                setTimeout(() => visPlaceholder.style.opacity = '1', 50);
                
                audioCtx = null;
                analyser = null;
            };
        };
    </script>
</body>
</html>";
        }
    }

}