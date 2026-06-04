using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
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

        // System Tray Components
        private System.Windows.Forms.NotifyIcon? notifyIcon;

        // Settings Profile Path
        private readonly string profilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings_profile.json");

        public MainWindow()
        {
            InitializeComponent();
            LoadDevices();
            InitializeNotifyIcon();
            this.StateChanged += MainWindow_StateChanged;
            this.Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Auto-load last saved settings on startup
            LoadSavedProfile();
        }

        // Local hotkeys: Works when the application is active/focused
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
                        // Set volume to 0 if muted, otherwise restore user fader setting
                        item.VolumeProvider.Volume = isMuted ? 0f : item.Volume;
                    }
                }
            }

            if (isMuted)
            {
                StatusText.Text = "Status: MUTED (Press M to Unmute)";
                StatusText.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#FFC400"); // Yellow warning
            }
            else
            {
                StatusText.Text = "Status: Connected and Routing Audio!";
                StatusText.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#55FF55");
            }
        }

        private void InitializeNotifyIcon()
        {
            notifyIcon = new System.Windows.Forms.NotifyIcon();
            notifyIcon.Text = "SoundSync";
            
            try
            {
                var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/logo.png"))?.Stream;
                if (iconStream != null)
                {
                    using (var ms = new MemoryStream())
                    {
                        iconStream.CopyTo(ms);
                        using (var bmp = new System.Drawing.Bitmap(ms))
                        {
                            notifyIcon.Icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
                        }
                    }
                }
            }
            catch
            {
                notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            notifyIcon.Visible = true;
            notifyIcon.DoubleClick += (s, e) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            };

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("Open SoundSync", null, (s, e) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            });
            contextMenu.Items.Add("Exit", null, (s, e) =>
            {
                System.Windows.Application.Current.Shutdown();
            });
            notifyIcon.ContextMenuStrip = contextMenu;
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && notifyIcon != null)
            {
                Hide(); 
                notifyIcon.ShowBalloonTip(2000, "SoundSync", "SoundSync is running in the system tray.", System.Windows.Forms.ToolTipIcon.Info);
            }
        }

        private void LoadDevices()
        {
            try
            {
                enumerator = new MMDeviceEnumerator();
                allDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                var items = allDevices.Select(d => new DeviceItem { Device = d, IsSelected = false }).ToList();
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
                        DelayMilliseconds = 0 // Initial value, relative calculations will override this immediately
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

                if (outputStreams.Count == 0)
                {
                    System.Windows.MessageBox.Show("Only the default device was selected. You must select a secondary device.");
                    Disconnect();
                    return;
                }

                // Calculate and apply initial relative delays
                UpdateRelativeDelays();

                loopbackCapture.DataAvailable += (s, args) =>
                {
                    foreach (var buffer in buffers)
                    {
                        if (buffer.BufferedDuration.TotalMilliseconds > 50)
                        {
                            buffer.ClearBuffer();
                        }
                        buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
                    }
                };
                loopbackCapture.StartRecording();

                isConnected = true;
                isMuted = false;
                ConnectButton.Content = "DISCONNECT";
                ConnectButton.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#CC3232");
                StatusText.Text = "Status: Connected and Routing Audio!";
                StatusText.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#55FF55");

                // Auto-save settings on successful connection
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
                stream.Stop();
                stream.Dispose();
            }

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
            ConnectButton.Content = "CONNECT";
            ConnectButton.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#007ACC");
            StatusText.Text = "Status: Disconnected";
            StatusText.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#FF5555");
        }

        // Relative Latency Offset Recalculator
        // Enables -200ms to 200ms sliders by delaying slower devices relatively
        public void UpdateRelativeDelays()
        {
            var items = DeviceListBox.ItemsSource as List<DeviceItem>;
            if (items == null) return;

            var activeItems = items.Where(i => i.IsSelected && i.DelayProvider != null).ToList();
            if (activeItems.Count == 0) return;

            // Find the lowest delay setting chosen by the user (-200ms to 200ms)
            int minDelaySetting = activeItems.Min(i => i.Delay);

            foreach (var item in activeItems)
            {
                if (item.DelayProvider != null)
                {
                    // Subtract the minimum delay so all delay values shift to positive millisecond offsets relative to each other
                    item.DelayProvider.DelayMilliseconds = item.Delay - minDelaySetting;
                }
            }
        }

        // Profile serialization logic
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
                notifyIcon.Visible = false;
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

                // Dispatch global relative calculations for active streams
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
                    
                    while (delayQueue.Count > delaySamples)
                    {
                        delayQueue.Dequeue();
                    }
                }
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
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
                    delayQueue.Enqueue(buffer[offset + i]);
                }

                while (delayQueue.Count < delaySamples)
                {
                    delayQueue.Enqueue(0f);
                }

                int written = 0;
                while (written < read && delayQueue.Count > 0)
                {
                    buffer[offset + written] = delayQueue.Dequeue();
                    written++;
                }

                return written;
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
}