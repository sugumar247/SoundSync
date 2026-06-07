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

        // System Tray Components
        private System.Windows.Forms.NotifyIcon? notifyIcon;

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
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSavedProfile();
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
                }
            }

            if (isMuted)
            {
                StatusText.Text = "Status: MUTED (Press M to Unmute)";
                StatusText.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#FFC400");
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
                else
                {
                    notifyIcon.Icon = System.Drawing.SystemIcons.Application;
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

                if (outputStreams.Count == 0)
                {
                    System.Windows.MessageBox.Show("Only the default device was selected. You must select a secondary device.");
                    Disconnect();
                    return;
                }

                // Calculate and apply initial relative delays
                UpdateRelativeDelays();

                // START ZERO-CONFIGURATION WIRELESS TCP/WEBSOCKET STREAM SERVER
                string ip = GetLocalIPAddress();
                int port = 8080;
                linkServer = new SoundSyncLinkServer(captureFormat.SampleRate);
                linkServer.Start(port);

                loopbackCapture.DataAvailable += (s, args) =>
                {
                    // Broadcast directly over local Wi-Fi link to connected browser clients
                    linkServer?.BroadcastAudio(args.Buffer, args.BytesRecorded);

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

                StatusText.Text = $"Streaming: http://{ip}:{port} | Routing Audio!";
                StatusText.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom("#55FF55");

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

            if (linkServer != null)
            {
                linkServer.Stop();
                linkServer = null;
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
    <title>SoundSync Link</title>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <link href='https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;600;700&family=JetBrains+Mono:wght@400;700&display=swap' rel='stylesheet'>
    <style>
        :root {
            --bg: #0A0B0E;
            --surface-blur: rgba(18, 20, 26, 0.7);
            --surface-border: rgba(255, 255, 255, 0.06);
            --accent: #00E5FF;
            --accent-glow: rgba(0, 229, 255, 0.25);
            --success: #00E676;
            --text-primary: #FFFFFF;
            --text-secondary: #90A4AE;
        }

        * {
            box-sizing: border-box;
            margin: 0;
            padding: 0;
            user-select: none;
        }

        body {
            background-color: var(--bg);
            color: var(--text-primary);
            font-family: 'Outfit', -apple-system, sans-serif;
            display: flex;
            align-items: center;
            justify-content: center;
            min-height: 100vh;
            overflow: hidden;
            position: relative;
        }

        /* Ambient Glow Background Backgrounds */
        .ambient-glow {
            position: absolute;
            width: 600px;
            height: 600px;
            background: radial-gradient(circle, rgba(0, 229, 255, 0.1) 0%, rgba(0, 0, 0, 0) 70%);
            top: 50%;
            left: 50%;
            transform: translate(-50%, -50%);
            z-index: 1;
            pointer-events: none;
            transition: all 0.8s ease;
        }

        .ambient-glow.active {
            background: radial-gradient(circle, rgba(0, 230, 118, 0.12) 0%, rgba(0, 0, 0, 0) 70%);
            width: 800px;
            height: 800px;
        }

        /* Premium Glass Card Container */
        .glass-card {
            position: relative;
            z-index: 10;
            width: 90%;
            max-width: 480px;
            background: var(--surface-blur);
            backdrop-filter: blur(24px);
            -webkit-backdrop-filter: blur(24px);
            border: 1px solid var(--surface-border);
            border-radius: 32px;
            padding: 40px;
            box-shadow: 0 30px 60px rgba(0, 0, 0, 0.6), inset 0 1px 0 rgba(255, 255, 255, 0.1);
            text-align: center;
            overflow: hidden;
        }

        .header-logo {
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 12px;
            margin-bottom: 24px;
        }

        .logo-icon {
            width: 36px;
            height: 36px;
            border-radius: 10px;
            background: linear-gradient(135deg, var(--accent), #00A6FF);
            display: flex;
            align-items: center;
            justify-content: center;
            box-shadow: 0 0 20px rgba(0, 229, 255, 0.4);
            animation: pulse 2s infinite alternate;
        }

        .logo-text {
            font-size: 24px;
            font-weight: 700;
            letter-spacing: -0.5px;
            background: linear-gradient(135deg, #FFFFFF, #B0BEC5);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
        }

        /* Status HUD Group */
        .status-hud {
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 16px;
            margin: 24px 0;
            background: rgba(255, 255, 255, 0.02);
            border: 1px solid rgba(255, 255, 255, 0.04);
            border-radius: 18px;
            padding: 16px;
        }

        .hud-item {
            text-align: left;
        }

        .hud-label {
            font-size: 11px;
            text-transform: uppercase;
            letter-spacing: 1.5px;
            color: var(--text-secondary);
            margin-bottom: 4px;
        }

        .hud-value {
            font-family: 'JetBrains Mono', monospace;
            font-size: 15px;
            font-weight: 600;
            color: #ECEFF1;
        }

        /* Visualizer Area */
        .visualizer-container {
            position: relative;
            width: 100%;
            height: 140px;
            background: rgba(0, 0, 0, 0.2);
            border: 1px solid rgba(255, 255, 255, 0.03);
            border-radius: 20px;
            margin: 28px 0;
            overflow: hidden;
            display: flex;
            align-items: center;
            justify-content: center;
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
            color: rgba(255, 255, 255, 0.15);
            font-size: 13px;
            letter-spacing: 0.5px;
            z-index: 2;
            transition: opacity 0.3s ease;
            display: flex;
            flex-direction: column;
            align-items: center;
            gap: 8px;
        }

        .visualizer-placeholder svg {
            width: 32px;
            height: 32px;
            stroke: currentColor;
            fill: none;
            stroke-width: 1.5;
        }

        /* Primary Action Button */
        .btn-connect {
            position: relative;
            background: linear-gradient(135deg, var(--accent), #0088FF);
            color: #050608;
            border: none;
            width: 100%;
            padding: 20px;
            font-size: 16px;
            font-weight: 700;
            border-radius: 20px;
            cursor: pointer;
            transition: all 0.3s cubic-bezier(0.25, 0.8, 0.25, 1);
            box-shadow: 0 6px 20px rgba(0, 229, 255, 0.3);
            letter-spacing: 0.5px;
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 10px;
        }

        .btn-connect:hover {
            transform: translateY(-2px);
            box-shadow: 0 8px 30px rgba(0, 229, 255, 0.5);
            filter: brightness(1.1);
        }

        .btn-connect:active {
            transform: translateY(1px);
        }

        .btn-connect.active {
            background: linear-gradient(135deg, var(--success), #00C853);
            color: #050608;
            box-shadow: 0 6px 24px rgba(0, 230, 118, 0.35);
        }

        @keyframes pulse {
            0% { transform: scale(1); box-shadow: 0 0 15px rgba(0, 229, 255, 0.4); }
            100% { transform: scale(1.05); box-shadow: 0 0 25px rgba(0, 229, 255, 0.6); }
        }

        .ripple-effect {
            position: absolute;
            border-radius: 50%;
            background: rgba(255, 255, 255, 0.4);
            transform: scale(0);
            animation: ripple 0.6s linear;
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
                <svg width='18' height='18' viewBox='0 0 24 24' fill='none' stroke='#050608' stroke-width='2.5' stroke-linecap='round' stroke-linejoin='round'>
                    <polygon points='11 5 6 9 2 9 2 15 6 15 11 19 11 5'></polygon>
                    <path d='M19.07 4.93a10 10 0 0 1 0 14.14M15.54 8.46a5 5 0 0 1 0 7.07'></path>
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
            ctx.strokeStyle = 'rgba(255, 255, 255, 0.02)';
            ctx.lineWidth = 1;
            for (let i = 20; i < width; i += 20) {
                ctx.beginPath();
                ctx.moveTo(i, 0);
                ctx.lineTo(i, height);
                ctx.stroke();
            }

            // Draw sound waveform
            ctx.lineWidth = 2.5;
            const grad = ctx.createLinearGradient(0, 0, width, 0);
            grad.addColorStop(0, '#00E5FF');
            grad.addColorStop(0.5, '#00E676');
            grad.addColorStop(1, '#00E5FF');
            ctx.strokeStyle = grad;
            ctx.shadowBlur = 10;
            ctx.shadowColor = 'rgba(0, 229, 255, 0.4)';

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
            ctx.shadowBlur = 0; // reset
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
            statusVal.innerText = 'NEGOTIATING';

            const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
            ws = new WebSocket(`${protocol}//${window.location.host}`);
            ws.binaryType = 'arraybuffer';

            ws.onopen = () => {
                btnText.innerText = 'STREAM ACTIVE';
                playBtn.classList.add('active');
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
                playBtn.style.background = '#FF5252';
                statusVal.innerText = 'CLOSED';
                statusVal.style.color = '#FF5252';
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