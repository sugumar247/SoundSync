using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SoundSync.Services
{
    public class NetworkStreamer : INetworkStreamer
    {
        private TcpListener? tcpListener;
        private readonly List<WebSocket> clients = new List<WebSocket>();
        private readonly object clientLock = new object();
        private bool isRunning = false;
        private string htmlPage = string.Empty;
        private CancellationTokenSource? cts;

        /// <summary>Everyone currently listening, browsers and players alike.</summary>
        private readonly List<Models.LinkClient> connectedClients = new List<Models.LinkClient>();

        /// <summary>Raised when a listener joins or leaves, so the UI can refresh.</summary>
        public event Action? ClientsChanged;

        public List<Models.LinkClient> GetClients()
        {
            lock (clientLock) { return new List<Models.LinkClient>(connectedClients); }
        }

        private readonly Dictionary<Models.LinkClient, WebSocket> controlSockets = new();

        /// <summary>Raised for every command a listener's page sends back.</summary>
        public event Action<string>? CommandReceived;

        /// <summary>Pushes a text message to every connected browser.</summary>
        public void SendToAll(string json)
        {
            WebSocket[] targets;
            lock (clientLock) { targets = clients.ToArray(); }

            var bytes = Encoding.UTF8.GetBytes(json);
            foreach (var socket in targets)
            {
                if (socket.State != WebSocketState.Open) continue;
                try
                {
                    _ = socket.SendAsync(new ArraySegment<byte>(bytes),
                                         WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch { }
            }
        }

        private readonly Dictionary<Models.LinkClient, long> lastSent = new();

        /// <summary>
        /// Sends a listener its own state: volume and whether it is following the PC.
        ///
        /// Rate limited to ten a second. Dragging a slider raises hundreds of changes, and
        /// every one of them would otherwise become a frame on a phone's Wi-Fi link, which
        /// is enough traffic to stutter the audio the same socket is carrying.
        /// </summary>
        private void SendControl(Models.LinkClient entry)
        {
            WebSocket? socket;
            lock (clientLock)
            {
                controlSockets.TryGetValue(entry, out socket);

                long now = Environment.TickCount64;
                lastSent.TryGetValue(entry, out long previous);
                if (now - previous < 100) return;
                lastSent[entry] = now;
            }
            if (socket == null || socket.State != WebSocketState.Open) return;

            var culture = System.Globalization.CultureInfo.InvariantCulture;
            string json = "{\"type\":\"listener\",\"volume\":"
                          + entry.Volume.ToString(culture)
                          + ",\"sync\":" + (entry.SyncVolumeWithDefault ? "true" : "false") + "}";

            var bytes = Encoding.UTF8.GetBytes(json);
            _ = socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        /// <summary>Pushes a listener's state now, ignoring the rate limit.</summary>
        public void SendListenerState(Models.LinkClient entry)
        {
            lock (clientLock) { lastSent[entry] = 0; }
            SendControl(entry);
        }

        private Models.LinkClient RegisterClient(TcpClient tcpClient, List<string> headers, string kind)
        {
            string ua = headers.FirstOrDefault(h => h.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase))
                               ?.Substring(11).Trim() ?? string.Empty;

            var entry = new Models.LinkClient
            {
                Address = (tcpClient.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?",
                DeviceName = Models.LinkClient.DescribeUserAgent(ua),
                Kind = kind
            };

            entry.VolumeChanged = SendControl;
            entry.SyncChanged = SendListenerState;
            lock (clientLock) { connectedClients.Add(entry); }
            ClientsChanged?.Invoke();
            return entry;
        }

        private void UnregisterClient(Models.LinkClient? entry)
        {
            if (entry == null) return;
            lock (clientLock) { connectedClients.Remove(entry); controlSockets.Remove(entry); lastSent.Remove(entry); }
            ClientsChanged?.Invoke();
        }

        /// <summary>Plain HTTP listeners - VLC and the like - receiving an endless WAV.</summary>
        private readonly List<NetworkStream> rawClients = new List<NetworkStream>();
        private int streamSampleRate = 48000;
        private int streamChannels = 2;

        /// <summary>Converts the captured float samples to 16-bit PCM and pushes them out.</summary>
        private void BroadcastToRawClients(byte[] buffer, int count)
        {
            NetworkStream[] targets;
            lock (clientLock)
            {
                if (rawClients.Count == 0) return;
                targets = rawClients.ToArray();
            }

            int samples = count / 4;
            var pcm = new byte[samples * 2];
            for (int i = 0; i < samples; i++)
            {
                float v = BitConverter.ToSingle(buffer, i * 4);
                short s = (short)(Math.Clamp(v, -1f, 1f) * short.MaxValue);
                pcm[i * 2] = (byte)(s & 0xFF);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }

            foreach (var target in targets)
            {
                try
                {
                    target.Write(pcm, 0, pcm.Length);
                }
                catch
                {
                    // Listener hung up or stopped reading: drop it and carry on.
                    lock (clientLock) { rawClients.Remove(target); }
                    try { target.Dispose(); } catch { }
                }
            }
        }

        public bool IsRunning => isRunning;

        public void Start(int sampleRate, int channels, int port)
        {
            streamSampleRate = sampleRate;
            streamChannels = Math.Max(1, channels);

            htmlPage = GetHtmlTemplate()
                .Replace("{{SAMPLE_RATE}}", sampleRate.ToString())
                .Replace("{{CHANNELS}}", Math.Max(1, channels).ToString())
                .Replace("{{TOKEN}}", LinkAuth.Token);
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
                List<string> headers = new List<string>();
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    headers.Add(line);
                }

                if (headers.Count == 0) return;

                // Every request - page load and WebSocket upgrade alike - must carry the
                // access token derived from the operator's SSH key. See LinkAuth.
                if (!LinkAuth.IsRequestAuthorized(headers[0]))
                {
                    byte[] deniedBody = Encoding.UTF8.GetBytes("401 Unauthorized");
                    string deniedHeader = "HTTP/1.1 401 Unauthorized\r\n" +
                                          "Content-Type: text/plain; charset=utf-8\r\n" +
                                          $"Content-Length: {deniedBody.Length}\r\n" +
                                          "Connection: close\r\n\r\n";

                    byte[] deniedHeaderBytes = Encoding.UTF8.GetBytes(deniedHeader);
                    await stream.WriteAsync(deniedHeaderBytes, 0, deniedHeaderBytes.Length, token);
                    await stream.WriteAsync(deniedBody, 0, deniedBody.Length, token);
                    return;
                }

                string requestTarget = headers[0].Split(' ').Length > 1 ? headers[0].Split(' ')[1] : "/";
                string requestPath = requestTarget.Split('?')[0];

                // Endless WAV for ordinary media players. VLC, foobar2000 and friends cannot
                // speak WebSocket, but they will happily play a never-ending HTTP audio
                // stream - the same trick internet radio uses.
                if (requestPath.Equals("/stream.wav", StringComparison.OrdinalIgnoreCase))
                {
                    var playerEntry = RegisterClient(tcpClient, headers, "player");
                    try { await ServeRawStreamAsync(stream, token); }
                    finally { UnregisterClient(playerEntry); }
                    return;
                }

                // A one-line playlist, for players that want a file to open rather than a URL.
                if (requestPath.Equals("/stream.m3u", StringComparison.OrdinalIgnoreCase))
                {
                    // Build the URL from the socket's own local address, never from the
                    // client-supplied Host header. Reflecting that header would let a
                    // poisoned request produce a playlist pointing somewhere else - with
                    // the access token inside it.
                    string host = tcpClient.Client.LocalEndPoint is IPEndPoint local
                        ? $"{local.Address}:{local.Port}"
                        : "localhost";
                    byte[] playlist = Encoding.UTF8.GetBytes(
                        "#EXTM3U\n#EXTINF:-1,SoundSync Live\n" +
                        $"http://{host}/stream.wav?t={LinkAuth.Token}\n");

                    string playlistHeader = "HTTP/1.1 200 OK\r\n" +
                                            "Content-Type: audio/x-mpegurl\r\n" +
                                            "Content-Disposition: attachment; filename=\"soundsync.m3u\"\r\n" +
                                            $"Content-Length: {playlist.Length}\r\n" +
                                            "Connection: close\r\n\r\n";
                    byte[] playlistHeaderBytes = Encoding.UTF8.GetBytes(playlistHeader);
                    await stream.WriteAsync(playlistHeaderBytes, 0, playlistHeaderBytes.Length, token);
                    await stream.WriteAsync(playlist, 0, playlist.Length, token);
                    return;
                }

                bool isWebSocket = headers.Any(h => h.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase));

                if (isWebSocket)
                {
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
                    var browserEntry = RegisterClient(tcpClient, headers, "browser");
                    lock (clientLock) { controlSockets[browserEntry] = webSocket; }

                    byte[] receiveBuffer = new byte[8192];
                    while (webSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
                    {
                        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }

                        // Text frames coming the other way are commands from the page.
                        if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                        {
                            string command = Encoding.UTF8.GetString(receiveBuffer, 0, result.Count);
                            CommandReceived?.Invoke(command);
                        }
                    }

                    lock (clientLock)
                    {
                        clients.Remove(webSocket);
                    }
                    UnregisterClient(browserEntry);
                    try { await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); } catch { }
                }
                else
                {
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
                // Client disconnected
            }
            finally
            {
                tcpClient.Close();
            }
        }

        /// <summary>
        /// Holds an ordinary media player on the line, feeding it an endless WAV.
        ///
        /// The header claims a near-infinite length because the real one is unknowable - the
        /// stream ends when the listener leaves. Samples are converted to 16-bit PCM, which
        /// every player understands and which halves the bandwidth compared with the float
        /// data the browser page receives.
        /// </summary>
        private async Task ServeRawStreamAsync(NetworkStream stream, CancellationToken token)
        {
            int rate = streamSampleRate;
            int ch = streamChannels;
            int byteRate = rate * ch * 2;

            var header = new List<byte>();
            void Ascii(string s) => header.AddRange(Encoding.ASCII.GetBytes(s));
            void U32(uint v) => header.AddRange(BitConverter.GetBytes(v));
            void U16(ushort v) => header.AddRange(BitConverter.GetBytes(v));

            Ascii("RIFF"); U32(0xFFFFFFFF); Ascii("WAVE");
            Ascii("fmt "); U32(16); U16(1); U16((ushort)ch);
            U32((uint)rate); U32((uint)byteRate); U16((ushort)(ch * 2)); U16(16);
            Ascii("data"); U32(0xFFFFFFFF);

            string httpHeader = "HTTP/1.1 200 OK\r\n" +
                                "Content-Type: audio/wav\r\n" +
                                "Cache-Control: no-cache, no-store\r\n" +
                                "Connection: close\r\n\r\n";
            byte[] httpHeaderBytes = Encoding.UTF8.GetBytes(httpHeader);

            await stream.WriteAsync(httpHeaderBytes, 0, httpHeaderBytes.Length, token);
            await stream.WriteAsync(header.ToArray(), 0, header.Count, token);
            await stream.FlushAsync(token);

            lock (clientLock) { rawClients.Add(stream); }
            try
            {
                // Nothing more to send from here: BroadcastAudio writes into this stream.
                // Park until the listener goes away or the server stops.
                while (isRunning && !token.IsCancellationRequested)
                {
                    await Task.Delay(250, token);
                    lock (clientLock) { if (!rawClients.Contains(stream)) break; }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                lock (clientLock) { rawClients.Remove(stream); }
            }
        }

        public void BroadcastAudio(byte[] buffer, int count)
        {
            BroadcastToRawClients(buffer, count);

            if (clients.Count == 0 || !isRunning) return;

            lock (clientLock)
            {
                for (int i = clients.Count - 1; i >= 0; i--)
                {
                    var client = clients[i];
                    if (client.State == WebSocketState.Open)
                    {
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
                int sampleCount = count / 4;
                float[] floatBuffer = new float[sampleCount];
                Buffer.BlockCopy(buffer, 0, floatBuffer, 0, count);

                byte[] binaryPayload = new byte[floatBuffer.Length * 4];
                Buffer.BlockCopy(floatBuffer, 0, binaryPayload, 0, binaryPayload.Length);

                await client.SendAsync(new ArraySegment<byte>(binaryPayload), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            catch
            {
                // Send failure
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

                foreach (var raw in rawClients)
                {
                    try { raw.Dispose(); } catch { }
                }
                rawClients.Clear();
                connectedClients.Clear();
            }
            ClientsChanged?.Invoke();

            try
            {
                tcpListener?.Stop();
            }
            catch { }
        }

        private string GetHtmlTemplate()
        {
            // Keep original template exact
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
        html {
            /* The body alone does not clip an absolutely positioned child that is wider
               than the screen, which is what let the whole page slide sideways. */
            overflow-x: hidden;
            max-width: 100%;
        }
        body {
            max-width: 100%;
            background-color: var(--bg);
            background-image: 
                radial-gradient(circle at 50% 50%, rgba(20, 15, 12, 0.8) 0%, rgba(10, 8, 8, 0.95) 100%),
                repeating-linear-gradient(0deg, rgba(0,0,0,0.08) 0px, rgba(0,0,0,0.08) 1px, transparent 1px, transparent 2px);
            color: var(--text-primary);
            font-family: 'Georgia', serif;
            display: flex;
            align-items: flex-start;
            justify-content: center;
            min-height: 100vh;
            /* The page grew past one screen once it carried the controls and the PC list,
               and a phone in portrait could not reach the buttons at all. */
            overflow-y: auto;
            overflow-x: hidden;
            -webkit-overflow-scrolling: touch;
            padding: 16px 0 32px 0;
            position: relative;
        }
        .ambient-glow {
            position: fixed;
            pointer-events: none;
            width: min(700px, 100vw);
            height: min(700px, 100vw);
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
        .controls {
            width: 100%; max-width: 520px; margin: 0 auto 18px auto;
            border: 1px solid var(--border); background: rgba(0,0,0,0.25); padding: 14px 16px;
        }
        .ctl-check { display: flex; align-items: center; gap: 8px; cursor: pointer;
                     font-family: 'JetBrains Mono', monospace; font-size: 12px;
                     letter-spacing: 1px; color: var(--text-primary); flex: 1; }
        .ctl-check input { width: 18px; height: 18px; accent-color: var(--accent); }
        .ctl-row.disabled { opacity: 0.35; pointer-events: none; }
        .ctl-row { display: flex; align-items: center; gap: 12px; margin-bottom: 10px; }
        .ctl-label { font-family: 'JetBrains Mono', monospace; font-size: 12px;
                     letter-spacing: 1px; color: var(--text-secondary); width: 62px; }
        .ctl-row input[type=range] { flex: 1; accent-color: var(--accent); height: 26px; }
        .ctl-value { font-family: 'JetBrains Mono', monospace; font-size: 12px;
                     color: var(--text-primary); width: 58px; text-align: right; }
        .ctl-hint { font-size: 11px; line-height: 1.5; color: var(--text-secondary);
                    margin: 6px 0 0 0; }
        .hud-value {
            font-family: 'JetBrains Mono', monospace;
            font-size: 16px;
            font-weight: 700;
            color: var(--text-primary);
        }
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
        <div class='controls' id='controls' style='display:none'>
            <div class='ctl-row'>
                <label class='ctl-check'>
                    <input type='checkbox' id='syncCtl'>
                    <span>REAL-TIME SYNC</span>
                </label>
                <span class='ctl-value' id='syncVal'></span>
            </div>
            <p class='ctl-hint' id='syncHint'>Off: the buffer below is whatever you set, and if the
               stream falls behind - locking the phone, a weak signal - it stays behind.
               On: playback speeds up by up to 2% until it catches up with the PC, and the buffer
               is chosen for you. A gap too large to stretch away is skipped instead.</p>
            <div class='ctl-row'>
                <label class='ctl-check'>
                    <input type='checkbox' id='vsyncCtl'>
                    <span>VOLUME SYNC WITH PC</span>
                </label>
            </div>
            <p class='ctl-hint'>Off: this device's volume is its own, and the PC's volume does
               not touch it. On: it follows the PC, keeping the balance it had when you ticked
               it. The same tick appears next to this listener on the PC, and either side can
               change it.</p>
            <div class='ctl-row'>
                <label class='ctl-label' for='volCtl'>VOLUME</label>
                <input type='range' id='volCtl' min='0' max='150' value='100'>
                <span class='ctl-value' id='volVal'>100%</span>
            </div>
            <div class='ctl-row'>
                <label class='ctl-label' for='bufCtl'>BUFFER</label>
                <input type='range' id='bufCtl' min='20' max='500' step='10' value='50'>
                <span class='ctl-value' id='bufVal'>50 ms</span>
            </div>
            <p class='ctl-hint'>Volume is local to this device and changes nothing on the PC.
               Buffer trades delay against dropouts: lower is tighter, higher survives a weak signal.</p>
        </div>

        <div class='controls' id='pcPanel' style='display:none'>
            <div class='ctl-row'><span class='ctl-label' style='width:auto'>PC OUTPUTS</span>
                <span class='ctl-value' id='pcMode' style='width:auto'></span></div>
            <div id='pcList'></div>
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
        let gainNode = null;
        let mediaDest = null;
        let silentBypass = null;
        let leadSeconds = 0.05;
        const controls = document.getElementById('controls');
        const volCtl = document.getElementById('volCtl');
        const volVal = document.getElementById('volVal');
        const bufCtl = document.getElementById('bufCtl');
        const bufVal = document.getElementById('bufVal');
        const syncCtl = document.getElementById('syncCtl');
        const syncVal = document.getElementById('syncVal');
        const bufRow = bufCtl.parentElement;

        // Target lead while real-time sync is driving, and the point past which a gap is
        // skipped rather than stretched away - 5% of speed-up would take a minute to eat a
        // ten second backlog, so a big one is jumped instead.
        const AUTO_TARGET = 0.05;
        // Catching up by dropping tiny slices rather than by playing faster. playbackRate
        // always moves the pitch - 2% is a third of a semitone and still audible on music -
        // and the Web Audio API has no pitch preserving stretch. Removing a few milliseconds
        // leaves the speed, and therefore the pitch, exactly alone.
        const MAX_TRIM_MS = 15;      // never take more than this out of one packet
        const TRIM_FADE_MS = 3;      // crossfade over the seam so it does not click
        const SKIP_THRESHOLD = 1.0;
        let autoSync = false;

        // The choice survives a reload, and comes back on next time it was left on.
        try { autoSync = localStorage.getItem('soundsync.realtime') === '1'; } catch (e) {}
        syncCtl.checked = autoSync;

        // Removes the quietest span of `wantMs` from a packet, joined with a short
        // crossfade. Speed and therefore pitch are untouched - a few milliseconds simply
        // never get played. Choosing the quietest part, and fading across the seam, is what
        // keeps it from being heard as a click or a stutter.
        function trimQuietest(data, channels, wantMs) {
            const rate = {{SAMPLE_RATE}};
            const frames = data.length / channels;
            let cutFrames = Math.floor(wantMs * rate / 1000);
            const fadeFrames = Math.floor(TRIM_FADE_MS * rate / 1000);

            // Leave enough either side to fade across, otherwise skip this packet.
            if (cutFrames < 1 || frames < cutFrames + fadeFrames * 2 + 8) return data;

            // Walk the packet in windows the size of the cut and keep the quietest one.
            const step = Math.max(1, Math.floor(cutFrames / 4));
            let bestStart = fadeFrames, bestEnergy = Infinity;
            for (let f = fadeFrames; f + cutFrames + fadeFrames <= frames; f += step) {
                let energy = 0;
                for (let i = f; i < f + cutFrames; i += Math.max(1, Math.floor(cutFrames / 32))) {
                    const v = data[i * channels];
                    energy += v * v;
                }
                if (energy < bestEnergy) { bestEnergy = energy; bestStart = f; }
            }

            const out = new Float32Array((frames - cutFrames) * channels);
            // Everything before the cut.
            out.set(data.subarray(0, bestStart * channels), 0);
            // Everything after it.
            out.set(data.subarray((bestStart + cutFrames) * channels), bestStart * channels);

            // Fade the join so the two sides meet without a step in the waveform.
            for (let f = 0; f < fadeFrames; f++) {
                const w = f / fadeFrames;
                for (let c = 0; c < channels; c++) {
                    const at = (bestStart - fadeFrames + f) * channels + c;
                    if (at < 0 || at >= out.length) continue;
                    const after = (bestStart + f) * channels + c;
                    if (after >= out.length) continue;
                    out[at] = out[at] * (1 - w) + out[after] * w;
                }
            }
            return out;
        }

        function applySyncMode() {
            autoSync = syncCtl.checked;
            try { localStorage.setItem('soundsync.realtime', autoSync ? '1' : '0'); } catch (e) {}
            if (autoSync) {
                bufRow.classList.add('disabled');
                bufCtl.disabled = true;
                bufVal.innerText = 'auto';
            } else {
                bufRow.classList.remove('disabled');
                bufCtl.disabled = false;
                bufVal.innerText = bufCtl.value + ' ms';
                leadSeconds = bufCtl.value / 1000;
                syncVal.innerText = '';
            }
        }
        syncCtl.addEventListener('change', applySyncMode);
        applySyncMode();

        volCtl.addEventListener('input', () => {
            volVal.innerText = volCtl.value + '%';
            if (gainNode) gainNode.gain.value = volCtl.value / 100;
            if (!applyingListenerState) sendVolume(volCtl.value / 100);
        });
        volCtl.addEventListener('change', () => {
            lastVolSend = 0;
            if (!applyingListenerState) sendVolume(volCtl.value / 100);
        });
        bufCtl.addEventListener('input', () => {
            bufVal.innerText = bufCtl.value + ' ms';
            leadSeconds = bufCtl.value / 1000;
        });
        let ws = null;
        let startTime = 0;
        let analyser = null;
        let dataArray = null;
        function resizeCanvas() {
            canvas.width = visualizerBox.clientWidth * window.devicePixelRatio;
            canvas.height = visualizerBox.clientHeight * window.devicePixelRatio;
            ctx.scale(window.devicePixelRatio, window.devicePixelRatio);
        }
        window.addEventListener('resize', resizeCanvas);
        resizeCanvas();
        function drawVisualizer() {
            if (!analyser) return;
            requestAnimationFrame(drawVisualizer);
            analyser.getByteTimeDomainData(dataArray);
            const width = canvas.width / window.devicePixelRatio;
            const height = canvas.height / window.devicePixelRatio;
            ctx.clearRect(0, 0, width, height);
            ctx.strokeStyle = 'rgba(234, 221, 201, 0.02)';
            ctx.lineWidth = 1;
            for (let i = 20; i < width; i += 20) {
                ctx.beginPath();
                ctx.moveTo(i, 0);
                ctx.lineTo(i, height);
                ctx.stroke();
            }
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
        const pcPanel = document.getElementById('pcPanel');
        const pcList = document.getElementById('pcList');
        const pcMode = document.getElementById('pcMode');
        let pcControllable = false;
        const vsyncCtl = document.getElementById('vsyncCtl');
        let applyingListenerState = false;

        vsyncCtl.addEventListener('change', () => {
            if (applyingListenerState) return;
            sendCommand({ type: 'listenerSync', value: vsyncCtl.checked });
        });

        // Dragging raises a change per pixel; ten a second is enough to feel immediate
        // without turning the control channel into a flood on a phone's link.
        let lastVolSend = 0;

        // While a finger or a mouse is on a control, updates from the PC are held back for
        // it. They are legitimate - a volume tied to the PC really did move - but applying
        // one mid-drag fights the person doing the dragging. The lock lifts shortly after
        // the last interaction, and the next update from the PC lands normally.
        let holdUntil = 0;
        const HOLD_MS = 700;
        function touching() { return Date.now() < holdUntil; }
        function holdNow() { holdUntil = Date.now() + HOLD_MS; }

        ['pointerdown','pointermove','touchstart','touchmove','mousedown','input']
            .forEach(evt => volCtl.addEventListener(evt, holdNow));
        function sendVolume(value) {
            const now = Date.now();
            if (now - lastVolSend < 100) return;
            lastVolSend = now;
            sendCommand({ type: 'listenerVolume', value: value });
        }

        function sendCommand(obj) {
            if (ws && ws.readyState === 1) ws.send(JSON.stringify(obj));
        }

        // Ten a second per control while it is being moved, and always one final send when
        // it is let go, so the value that sticks is the one the person actually chose.
        function throttled(send) {
            let last = 0;
            return {
                during(value) {
                    const now = Date.now();
                    if (now - last < 100) return;
                    last = now;
                    send(value);
                },
                final(value) { last = 0; send(value); }
            };
        }

        // While a control on this page is being held, the PC's list is not redrawn. Rebuilding
        // it would replace the very element under the finger, which reads as the drag being
        // ignored until release.
        let listHoldUntil = 0;
        let pendingSnapshot = null;
        function holdList() { listHoldUntil = Date.now() + 700; }
        function listHeld() { return Date.now() < listHoldUntil; }
        setInterval(() => {
            if (pendingSnapshot && !listHeld()) {
                const snap = pendingSnapshot;
                pendingSnapshot = null;
                renderDevices(snap);
            }
        }, 200);

        // Draws the PC's outputs. Read-only unless the PC has remote control switched on.
        function renderDevices(msg) {
            pcControllable = !!msg.controllable;
            pcMode.innerText = pcControllable ? 'control enabled' : 'view only';
            pcPanel.style.display = 'block';
            pcList.innerHTML = '';

            msg.devices.forEach(d => {
                const row = document.createElement('div');
                row.className = 'ctl-row';

                const tick = document.createElement('input');
                tick.type = 'checkbox';
                tick.checked = !!d.selected;
                tick.disabled = !pcControllable || d.isDefault;
                tick.onchange = () => sendCommand({ type: 'select', id: d.id, value: tick.checked });

                const label = document.createElement('span');
                label.className = 'ctl-label';
                label.style.width = 'auto';
                label.style.flex = '1';
                label.innerText = d.name + (d.isDefault ? '  [SOURCE]' : '') + (d.badge ? '  ' + d.badge : '');

                const vol = document.createElement('input');
                vol.type = 'range'; vol.min = 0; vol.max = 100;
                vol.value = Math.round(d.volume * 100);
                vol.disabled = !pcControllable;
                const volSend = throttled(v => sendCommand({ type: 'volume', id: d.id, value: v }));
                vol.oninput = () => { pct.innerText = vol.value + '%'; holdList(); volSend.during(vol.value / 100); };
                vol.onchange = () => { holdList(); volSend.final(vol.value / 100); };

                const pct = document.createElement('span');
                pct.className = 'ctl-value';
                pct.innerText = Math.round(d.volume * 100) + '%';

                row.appendChild(tick); row.appendChild(label); row.appendChild(vol); row.appendChild(pct);
                pcList.appendChild(row);

                if (d.editable) {
                    const dRow = document.createElement('div');
                    dRow.className = 'ctl-row';
                    const dLab = document.createElement('span');
                    dLab.className = 'ctl-label'; dLab.innerText = 'DELAY';
                    const dSlide = document.createElement('input');
                    dSlide.type = 'range'; dSlide.min = 0; dSlide.max = 500; dSlide.value = d.delay;
                    dSlide.disabled = !pcControllable;
                    const delaySend = throttled(v => sendCommand({ type: 'delay', id: d.id, value: v }));
                    dSlide.oninput = () => { dVal.innerText = dSlide.value + ' ms'; holdList(); delaySend.during(parseInt(dSlide.value)); };
                    dSlide.onchange = () => { holdList(); delaySend.final(parseInt(dSlide.value)); };
                    const dVal = document.createElement('span');
                    dVal.className = 'ctl-value'; dVal.innerText = d.delay + ' ms';
                    dRow.appendChild(dLab); dRow.appendChild(dSlide); dRow.appendChild(dVal);
                    pcList.appendChild(dRow);
                }
            });
        }

        // Any later tap gets one more chance to unlock a context the browser left suspended.
        document.addEventListener('click', () => {
            if (audioCtx && audioCtx.state === 'suspended') {
                const again = audioCtx.resume();
                if (again && again.catch) again.catch(() => {});
            }
        });

        function teardown() {
            try { if (ws) { ws.onclose = null; ws.close(); } } catch (e) {}
            ws = null;
            try { if (silentBypass) { silentBypass.pause(); silentBypass.srcObject = null; } } catch (e) {}
            silentBypass = null; mediaDest = null; gainNode = null;
            try { if (audioCtx) audioCtx.close(); } catch (e) {}
            audioCtx = null;
            controls.style.display = 'none';
            pcPanel.style.display = 'none';
            btnText.innerText = 'CONNECT RECEIVER';
            playBtn.classList.remove('active');
            playBtn.style.background = '';
            statusVal.innerText = 'IDLE';
            statusVal.style.color = '';
            visualizerBox.classList.remove('active');
            bgGlow.classList.remove('active');
            visPlaceholder.style.display = '';
            visPlaceholder.style.opacity = '1';
        }

        playBtn.onclick = () => {
            // Second press disconnects, so one button covers both directions.
            if (audioCtx) { teardown(); return; }
            audioCtx = new (window.AudioContext || window.webkitAudioContext)();

            // Phones start an AudioContext suspended and only honour resume() from inside a
            // real user gesture. Resuming here - in the click handler - is what unlocks it;
            // doing it later, when audio arrives, is silently ignored and the page then shows
            // a healthy connection while playing nothing at all.
            const unlock = audioCtx.resume();
            if (unlock && unlock.catch) unlock.catch(() => {});

            // iOS additionally wants a buffer to have been played from within the gesture.
            try {
                const primer = audioCtx.createBufferSource();
                primer.buffer = audioCtx.createBuffer(1, 1, 22050);
                primer.connect(audioCtx.destination);
                primer.start(0);
            } catch (e) {}

            analyser = audioCtx.createAnalyser();
            analyser.fftSize = 256;
            dataArray = new Uint8Array(analyser.frequencyBinCount);

            gainNode = audioCtx.createGain();
            gainNode.gain.value = volCtl.value / 100;
            analyser.connect(gainNode);

            // iOS mutes the Web Audio API when the ringer switch is off, but it does NOT
            // mute a media element. Routing the graph into a <video> and playing that gets
            // sound out with the phone on silent - the trick every web player uses.
            // If the browser will not co-operate we fall back to the normal destination.
            let routed = false;
            try {
                if (audioCtx.createMediaStreamDestination) {
                    mediaDest = audioCtx.createMediaStreamDestination();
                    gainNode.connect(mediaDest);
                    silentBypass = document.createElement('video');
                    silentBypass.setAttribute('playsinline', '');
                    silentBypass.setAttribute('webkit-playsinline', '');
                    silentBypass.muted = false;
                    silentBypass.volume = 1.0;
                    silentBypass.srcObject = mediaDest.stream;
                    const played = silentBypass.play();
                    if (played && played.catch) played.catch(() => {
                        gainNode.connect(audioCtx.destination);
                    });
                    routed = true;
                }
            } catch (e) { routed = false; }
            if (!routed) gainNode.connect(audioCtx.destination);

            controls.style.display = 'block';
            btnText.innerText = 'ESTABLISHING...';
            playBtn.style.background = '';
            statusVal.innerText = 'NEGOTIATING';
            const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
            ws = new WebSocket(`${protocol}//${window.location.host}/?t={{TOKEN}}`);
            ws.binaryType = 'arraybuffer';
            ws.onopen = () => {
                btnText.innerText = 'STREAM ACTIVE';
                playBtn.classList.add('active');
                playBtn.style.background = '';
                if (audioCtx.state === 'suspended') {
                    statusVal.innerText = 'TAP AGAIN TO ALLOW AUDIO';
                    statusVal.style.color = 'var(--error)';
                } else {
                    statusVal.innerText = 'CONNECTED';
                    statusVal.style.color = 'var(--success)';
                }
                visualizerBox.classList.add('active');
                visPlaceholder.style.opacity = '0';
                bgGlow.classList.add('active');
                setTimeout(() => visPlaceholder.style.display = 'none', 300);
                drawVisualizer();
                sendCommand({ type: 'refresh' });
            };
            ws.onmessage = async (event) => {
                // Text frames are control messages from the PC, not audio.
                if (typeof event.data === 'string') {
                    try {
                        const msg = JSON.parse(event.data);
                        if (msg.type === 'devices') {
                            if (listHeld()) { pendingSnapshot = msg; }
                            else { renderDevices(msg); }
                            return;
                        }
                        if (msg.type === 'listener') {
                            applyingListenerState = true;
                            // The gain follows immediately either way - what is held back is
                            // moving the slider under the user's finger.
                            if (gainNode) gainNode.gain.value = msg.volume;
                            if (!touching()) {
                                const pct = Math.round(msg.volume * 100);
                                volCtl.value = pct;
                                volVal.innerText = pct + '%';
                            }
                            vsyncCtl.checked = !!msg.sync;
                            applyingListenerState = false;
                            return;
                        }
                        if (msg.type === 'volume') {
                            const pct = Math.round(msg.value * 100);
                            volCtl.value = pct;
                            volVal.innerText = pct + '%';
                            if (gainNode) gainNode.gain.value = msg.value;
                        }
                    } catch (e) {}
                    return;
                }
                if (audioCtx.state === 'suspended') {
                    audioCtx.resume();
                }
                const arrayBuffer = event.data;
                let floatData = new Float32Array(arrayBuffer);
                const channels = {{CHANNELS}}; 

                if (autoSync) {
                    const queued = startTime - audioCtx.currentTime;

                    if (queued > SKIP_THRESHOLD) {
                        // Bigger than anything worth shaving away a few milliseconds at a
                        // time: jump, and take the one audible gap instead of a long one.
                        startTime = audioCtx.currentTime + AUTO_TARGET;
                        syncVal.innerText = 'resync';
                    } else if (queued > AUTO_TARGET * 1.5) {
                        const wantMs = Math.min(MAX_TRIM_MS, (queued - AUTO_TARGET) * 1000);
                        floatData = trimQuietest(floatData, channels, wantMs);
                        syncVal.innerText = Math.round(queued * 1000) + ' ms, catching up';
                    } else {
                        syncVal.innerText = Math.round(queued * 1000) + ' ms';
                    }
                    bufVal.innerText = Math.round(Math.max(0, queued) * 1000) + ' ms';
                    bufCtl.value = Math.max(bufCtl.min, Math.min(bufCtl.max, Math.round(queued * 1000)));
                }

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
                bufferSource.connect(analyser);

                if (startTime < audioCtx.currentTime) {
                    // Ran dry: restart just ahead of now.
                    startTime = audioCtx.currentTime + (autoSync ? AUTO_TARGET : leadSeconds);
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
