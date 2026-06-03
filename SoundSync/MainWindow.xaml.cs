using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders; // Added for Volume Control
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundSync
{
    public partial class MainWindow : Window
    {
        private MMDeviceEnumerator enumerator;
        private List<MMDevice> allDevices;

        // NAudio Audio Components
        private WasapiLoopbackCapture loopbackCapture;
        private List<WasapiOut> outputStreams = new List<WasapiOut>();
        private List<BufferedWaveProvider> buffers = new List<BufferedWaveProvider>();
        private bool isConnected = false;

        public MainWindow()
        {
            InitializeComponent();
            LoadDevices();
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
                MessageBox.Show("Error loading devices: " + ex.Message);
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (isConnected)
            {
                MessageBox.Show("Please DISCONNECT first before refreshing the device list.");
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
            // Note: We now grab the full DeviceItem, not just the MMDevice, so we have access to its Volume settings!
            var items = (List<DeviceItem>)DeviceListBox.ItemsSource;
            var selectedDevices = items.Where(i => i.IsSelected).ToList();
            if (selectedDevices.Count == 0)
            {
                MessageBox.Show("Please check at least one device to connect.");
                return;
            }

            try
            {
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

                    // ---- VOLUME CONTROL ENGINE ----
                    // 1. Convert our raw bytes into float Samples
                    var sampleProvider = buffer.ToSampleProvider();
                    // 2. Attach a volume knob, setting it to whatever the slider is currently at
                    var volumeProvider = new VolumeSampleProvider(sampleProvider)
                    {
                        Volume = deviceItem.Volume
                    };
                    // 3. Save a reference to this knob so the Slider can update it while running!
                    deviceItem.VolumeProvider = volumeProvider;
                    // 4. Convert it back to a Wave format and pass it to WasapiOut
                    wasapiOut.Init(volumeProvider.ToWaveProvider());
                    // --------------------------------

                    wasapiOut.Play();
                    outputStreams.Add(wasapiOut);
                    buffers.Add(buffer);
                }

                if (outputStreams.Count == 0)
                {
                    MessageBox.Show("Only the default device was selected. You must select a secondary device.");
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
                MessageBox.Show("Error starting audio routing: " + ex.Message);
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

            // Unlink volume providers so sliders don't error out if dragged while disconnected
            if (DeviceListBox.ItemsSource != null)
            {
                foreach (var item in (List<DeviceItem>)DeviceListBox.ItemsSource)
                {
                    item.VolumeProvider = null;
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
            base.OnClosed(e);
        }
    }

    // Upgraded DeviceItem class handles UI updates from the slider
    public class DeviceItem : INotifyPropertyChanged
    {
        public MMDevice Device { get; set; }
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

                // If we are currently connected, update NAudio in real-time!
                if (VolumeProvider != null)
                {
                    VolumeProvider.Volume = _volume;
                }
            }
        }

        public VolumeSampleProvider VolumeProvider { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}