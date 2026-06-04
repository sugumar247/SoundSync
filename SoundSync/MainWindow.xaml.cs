using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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

        // System Tray Components
        private System.Windows.Forms.NotifyIcon? notifyIcon;

        public MainWindow()
        {
            InitializeComponent();
            LoadDevices();
            InitializeNotifyIcon();
            this.StateChanged += MainWindow_StateChanged;
        }

        private void InitializeNotifyIcon()
        {
            notifyIcon = new System.Windows.Forms.NotifyIcon();
            notifyIcon.Text = "SoundSync";
            
            // Use application icon if available
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

            // Add basic context menu to exit
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
                Hide(); // Hides window from taskbar, keeping it active in system tray
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
        }

        private void CheckBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (isConnected)
            {
                // Silently block the checkbox from being unchecked while running
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

                    // 3. Attach custom dynamic audio delay
                    var delayProvider = new DelaySampleProvider(volumeProvider)
                    {
                        DelayMilliseconds = deviceItem.Delay
                    };
                    deviceItem.DelayProvider = delayProvider;

                    // 4. Attach custom peak level VU meter
                    var meterProvider = new MeteringSampleProvider(delayProvider, (peak) =>
                    {
                        deviceItem.PeakLevel = peak;
                    });

                    // 5. Convert back to wave and initialize wasapiOut
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
                ConnectButton.Content = "DISCONNECT";
                ConnectButton.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#CC3232");
                StatusText.Text = "Status: Connected and Routing Audio!";
                StatusText.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#55FF55");
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

            // Unlink providers so sliders don't error out if dragged while disconnected
            var items = DeviceListBox.ItemsSource as List<DeviceItem>;
            if (items != null)
            {
                foreach (var item in items)
                {
                    item.VolumeProvider = null;
                    item.DelayProvider = null;
                    item.PeakLevel = 0f; // Reset VU meter
                }
            }

            outputStreams.Clear();
            buffers.Clear();

            isConnected = false;
            ConnectButton.Content = "CONNECT";
            ConnectButton.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#007ACC");
            StatusText.Text = "Status: Disconnected";
            StatusText.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#FF5555");
        }

        protected override void OnClosed(EventArgs e)
        {
            Disconnect();
            if (notifyIcon != null)
            {
                notifyIcon.Visible = false;
                notifyIcon.Dispose();
            }
            base.OnClosed(e);
        }
    }

    // Upgraded DeviceItem class handles UI updates from volume, delay, and peak level
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

        // Starts at 1.0f (100% Volume)
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

        // Delay in milliseconds (default 0ms)
        private int _delay = 0;
        public int Delay
        {
            get => _delay;
            set
            {
                _delay = value;
                OnPropertyChanged();

                if (DelayProvider != null)
                {
                    DelayProvider.DelayMilliseconds = _delay;
                }
            }
        }

        // Peak volume level for VU meter (0.0 to 1.0)
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
        public DelaySampleProvider? DelayProvider { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    // Custom Delay Sample Provider wrapper to line up Bluetooth and Wired latency
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

    // Custom Metering Sample Provider wrapper for real-time Peak Level tracking (VU Meter)
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

            // Smooth decay: Fast attack, slow release (standard envelope detection)
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