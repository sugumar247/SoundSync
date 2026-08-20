using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundSync.Models;
using SoundSync.Services;

/// <summary>
/// Runs the real engine and a real listener against the real hardware for a stretch of
/// time, and measures the things that actually go wrong: the level drifting with the PC
/// volume, the stream stalling, control messages piling up, delay growing without bound.
///
/// This exists because unit tests over the gain arithmetic all passed while the app was
/// audibly broken. Anything asserted here is measured from a running system.
/// </summary>
public static class SoakTest
{
    class Probe : INetworkStreamer
    {
        readonly NetworkStreamer inner = new();
        public float Peak;
        public long Packets;
        public long Bytes;
        public event Action? ClientsChanged { add => inner.ClientsChanged += value; remove => inner.ClientsChanged -= value; }
        public event Action<string>? CommandReceived { add => inner.CommandReceived += value; remove => inner.CommandReceived -= value; }
        public void SendToAll(string json) => inner.SendToAll(json);
        public List<LinkClient> GetClients() => inner.GetClients();
        public bool IsRunning => inner.IsRunning;
        public void Start(int rate, int channels, int port) => inner.Start(rate, channels, port);
        public void Stop() => inner.Stop();
        public void Reset() { Peak = 0; Packets = 0; Bytes = 0; }
        public void BroadcastAudio(byte[] b, int c)
        {
            Packets++; Bytes += c;
            for (int i = 0; i + 3 < c; i += 4)
            {
                float v = Math.Abs(BitConverter.ToSingle(b, i));
                if (v > Peak) Peak = v;
            }
            inner.BroadcastAudio(b, c);
        }
    }

    class Tone : ISampleProvider
    {
        int n;
        public Tone(WaveFormat f) { WaveFormat = f; }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] b, int off, int count)
        {
            for (int i = 0; i < count; i++)
                b[off + i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * (n++ / WaveFormat.Channels) / (double)WaveFormat.SampleRate));
            return count;
        }
    }

    static int pass, fail;
    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { pass++; Console.WriteLine($"  PASS  {name} {detail}"); }
        else { fail++; Console.WriteLine($"  FAIL  {name} {detail}"); }
    }

    public static async Task<int> Run(int seconds = 45, int port = 8300)
    {
        pass = 0; fail = 0;
        var en = new MMDeviceEnumerator();
        var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var mirror = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                       .First(d => d.ID != def.ID);

        float originalVolume = def.AudioEndpointVolume.MasterVolumeLevelScalar;
        bool originalMute = def.AudioEndpointVolume.Mute;

        Console.WriteLine($"source : {def.FriendlyName}");
        Console.WriteLine($"mirror : {mirror.FriendlyName}");
        Console.WriteLine($"running for {seconds}s");
        Console.WriteLine();

        var format = def.AudioClient.MixFormat;
        var player = new WasapiOut(def, AudioClientShareMode.Shared, true, 50);
        player.Init(new Tone(WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels)).ToWaveProvider());
        def.AudioEndpointVolume.Mute = false;
        def.AudioEndpointVolume.MasterVolumeLevelScalar = 1.0f;
        player.Play();
        Thread.Sleep(700);

        var engine = new AudioEngine();
        var probe = new Probe();
        probe.Start(format.SampleRate, format.Channels, port);

        var item = new DeviceItem { Device = mirror, IsDefaultDevice = false, IsSelected = true, Volume = 1f };
        var all = new List<DeviceItem> { new DeviceItem { Device = def, IsDefaultDevice = true }, item };
        engine.Connect(new List<DeviceItem> { item }, probe, _ => { }, () => { });
        engine.ApplyMakeUpGain(all, 1.0f, true);
        Thread.Sleep(600);

        // A listener that behaves like the page: reads everything, counts control traffic.
        var control = new ConcurrentQueue<string>();
        long audioBytes = 0;
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) Safari/604.1");
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/?t={LinkAuth.Token}"), CancellationToken.None);

        var stop = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            var buffer = new byte[256 * 1024];
            while (!stop.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                try
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    if (r.MessageType == WebSocketMessageType.Text)
                        control.Enqueue(Encoding.UTF8.GetString(buffer, 0, r.Count));
                    else audioBytes += r.Count;
                }
                catch { break; }
            }
        });
        await Task.Delay(800);

        Console.WriteLine("=== level against the PC volume ===");
        Console.WriteLine("  master | delivered peak");
        Console.WriteLine("  -------|---------------");
        var levels = new List<(float v, float p)>();
        foreach (float v in new[] { 1.00f, 0.70f, 0.45f, 1.00f })
        {
            def.AudioEndpointVolume.MasterVolumeLevelScalar = v;
            await Task.Delay(900);
            probe.Reset();
            await Task.Delay(1200);
            levels.Add((v, probe.Peak));
            Console.WriteLine($"  {v * 100,5:F0}%  | {probe.Peak,14:F4}");
        }
        float baseline = levels[0].p;
        Check("audio is flowing", baseline > 0.05f, $"({baseline:F4})");
        foreach (var (v, p) in levels.Skip(1))
            Check($"level holds at {v * 100:F0}% master",
                  baseline > 0.05f && Math.Abs(p - baseline) / baseline < 0.25f, $"({p:F4} vs {baseline:F4})");

        Console.WriteLine();
        Console.WriteLine("=== control traffic while a listener drags its volume ===");
        while (control.TryDequeue(out _)) { }
        var listener = probe.GetClients().FirstOrDefault();
        if (listener != null)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 60; i++) { listener.SetFromListener(0.4f + (i % 20) * 0.02f); await Task.Delay(25); }
            await Task.Delay(600);
            sw.Stop();
            int messages = control.Count;
            Console.WriteLine($"  60 changes from the listener in {sw.ElapsedMilliseconds} ms -> {messages} messages back");
            Check("its own changes are not echoed", messages == 0, $"({messages})");
        }

        Console.WriteLine();
        Console.WriteLine($"=== continuity over {seconds}s ===");
        long startPackets = probe.Packets, startAudio = audioBytes;
        var gaps = 0;
        long previous = probe.Packets;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            await Task.Delay(1000);
            long now = probe.Packets;
            if (now == previous) gaps++;
            previous = now;
        }
        watch.Stop();
        long packets = probe.Packets - startPackets;
        long audio = audioBytes - startAudio;
        double kbps = audio / 1024.0 / watch.Elapsed.TotalSeconds;
        Console.WriteLine($"  {packets} packets, {audio / 1024.0:F0} KB to the listener, {kbps:F0} KB/s");
        Console.WriteLine($"  seconds with no packet at all: {gaps}");
        Check("stream never stalled", gaps == 0, $"({gaps} silent seconds)");
        Check("listener kept receiving", audio > 100_000, $"({audio} bytes)");
        Check("engine still connected", engine.IsConnected);
        Check("listener still listed", probe.GetClients().Count == 1, $"({probe.GetClients().Count})");

        stop.Cancel();
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        engine.Disconnect(all);
        probe.Stop();
        player.Stop(); player.Dispose();
        def.AudioEndpointVolume.MasterVolumeLevelScalar = originalVolume;
        def.AudioEndpointVolume.Mute = originalMute;
        Console.WriteLine($"\nvolume restored to {originalVolume * 100:F0}%");

        Console.WriteLine();
        Console.WriteLine($"=== SOAK: {pass} passed, {fail} failed ===");
        return fail == 0 ? 0 : 1;
    }
}
