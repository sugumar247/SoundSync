using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VirtualAudioMixerGUI
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
            LoadDevices(); // Scan for devices as soon as the app starts
        }

        private void LoadDevices()
        {
            try
            {
                enumerator = new MMDeviceEnumerator();

                // 1. DEVICE ENUMERATION
                allDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

                // Bind them to the UI ListBox as DeviceItem objects so checkboxes appear
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
            // We shouldn't refresh the list if the audio is currently actively routing
            if (isConnected)
            {
                MessageBox.Show("Please DISCONNECT first before refreshing the device list.");
                return;
            }

            // Re-scan the computer for new devices
            LoadDevices();
        }

        // FIX: INTERCEPT CLICK EVENTS INSTEAD OF DISABLING THE CONTROL
        private void CheckBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (isConnected)
            {
                // Silently block the click event so users can't toggle items while connected, 
                // without triggering the ugly white disabled overlay style!
                e.Handled = true;
            }
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            // Toggle logic for the big button
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
            // 2. USER SELECTION
            var items = (List<DeviceItem>)DeviceListBox.ItemsSource;
            var selectedDevices = items.Where(i => i.IsSelected).Select(i => i.Device).ToList();

            if (selectedDevices.Count == 0)
            {
                MessageBox.Show("Please check at least one device to connect.");
                return;
            }

            try
            {
                var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                // 3. SYSTEM AUDIO CAPTURE (LOOPBACK)
                loopbackCapture = new WasapiLoopbackCapture();
                var captureFormat = loopbackCapture.WaveFormat;

                // 4. MULTI-ENDPOINT AUDIO ROUTING
                foreach (var device in selectedDevices)
                {
                    // FIX #1: Prevent the feedback loop!
                    if (device.ID == defaultDevice.ID)
                    {
                        continue;
                    }

                    var wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 50);

                    // FIX #2: Keep the bucket extremely small (150ms max)
                    var buffer = new BufferedWaveProvider(captureFormat)
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(150),
                        DiscardOnBufferOverflow = true
                    };

                    wasapiOut.Init(buffer);
                    wasapiOut.Play();
                    outputStreams.Add(wasapiOut);
                    buffers.Add(buffer);
                }

                if (outputStreams.Count == 0)
                {
                    MessageBox.Show("Only the default device was selected. You must select a secondary device.");
                    Disconnect(); // Reset
                    return;
                }

                // The audio routing event
                loopbackCapture.DataAvailable += (s, args) =>
                {
                    foreach (var buffer in buffers)
                    {
                        // FIX #3: Aggressive Latency Control
                        if (buffer.BufferedDuration.TotalMilliseconds > 50)
                        {
                            buffer.ClearBuffer();
                        }
                        buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
                    }
                };

                loopbackCapture.StartRecording();

                // Update the User Interface to show it is running
                isConnected = true;
                ConnectButton.Content = "DISCONNECT";

                ConnectButton.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#CC3232"); // Deep red
                StatusText.Text = "Status: Connected and Routing Audio!";
                StatusText.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#55FF55"); // Bright green

                // UI IsEnabled property is left untouched here to protect colors
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error starting audio routing: " + ex.Message +
                                "\n\nHint: If it says 'Unsupported Wave Format', your selected output device " +
                                "does not support the exact Sample Rate of your default playback device.");
                Disconnect();
            }
        }

        private void Disconnect()
        {
            // 5. CLEANUP
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

            outputStreams.Clear();
            buffers.Clear();

            // Reset the User Interface
            isConnected = false;
            ConnectButton.Content = "CONNECT";

            ConnectButton.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#007ACC"); // Original blue button
            StatusText.Text = "Status: Disconnected";
            StatusText.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#FF5555"); // Red text
        }

        protected override void OnClosed(EventArgs e)
        {
            Disconnect();
            base.OnClosed(e);
        }
    }

    public class DeviceItem
    {
        public MMDevice Device { get; set; }
        public bool IsSelected { get; set; }
        public string Name => Device.FriendlyName;
    }
}