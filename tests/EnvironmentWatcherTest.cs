using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using SoundSync.Services;

/// <summary>
/// Exercises the watcher that rebuilds mirroring when the audio setup changes, against the
/// real machine.
///
/// What it can prove depends on where it is run, and it says which:
///
///  - At the console, with more than one output, it switches the default output and back
///    and checks that the change was reported.
///  - Over Remote Desktop there is only ever one endpoint, so that check is skipped rather
///    than passed. The session's own connect and disconnect cannot be triggered from a test
///    without throwing the person using the machine out of their session, so it is not
///    attempted.
///
/// The rest holds anywhere: a volume change and a sample rate change must both leave the
/// session alone, and unregistering must be complete. Those three are the ones that would
/// otherwise tear down a working session, so a pass here is worth having even on a machine
/// where the first check has to be skipped.
/// </summary>
public static class EnvironmentWatcherTest
{
    static int pass, fail, skip;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { pass++; Console.WriteLine($"  PASS  {name} {detail}"); }
        else { fail++; Console.WriteLine($"  FAIL  {name} {detail}"); }
    }

    static void Skip(string name, string why)
    {
        skip++;
        Console.WriteLine($"  SKIP  {name} - {why}");
    }

    public static int Run()
    {
        pass = 0; fail = 0; skip = 0;

        var enumerator = new MMDeviceEnumerator();
        Console.WriteLine($"session {Process.GetCurrentProcess().SessionId}, render endpoints:");
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All))
            Console.WriteLine($"  {d.State,-12} {d.FriendlyName}");
        Console.WriteLine();

        var seen = new ConcurrentQueue<(AudioEnvironmentChange kind, string reason, long ms)>();
        var clock = Stopwatch.StartNew();
        var watcher = new AudioEnvironmentWatcher();
        watcher.Changed += (kind, reason) => seen.Enqueue((kind, reason, clock.ElapsedMilliseconds));
        watcher.Start();
        Thread.Sleep(400);

        var original = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var others = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                               .Where(d => d.ID != original.ID).ToList();

        Console.WriteLine("=== the default output moving must be reported ===");
        if (others.Count == 0)
        {
            Skip("default output change reported",
                 "this session has a single output, which is what Remote Desktop leaves behind");
        }
        else
        {
            while (seen.TryDequeue(out _)) { }
            bool accepted = SystemAudioConfig.SetAsDefault(others[0]);
            Thread.Sleep(1500);
            var reported = seen.Where(e => e.kind == AudioEnvironmentChange.DefaultDevice).ToList();

            Check("Windows accepted the switch", accepted, $"(to {others[0].FriendlyName})");
            Check("default output change reported", reported.Count > 0,
                  $"({reported.Count} events, first at {(reported.Count > 0 ? reported[0].ms + " ms" : "-")})");

            SystemAudioConfig.SetAsDefault(original);
            Thread.Sleep(1200);
            Check("default put back",
                  enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID == original.ID,
                  $"({original.FriendlyName})");
        }

        Console.WriteLine();
        Console.WriteLine("=== a volume change must not rebuild anything ===");
        while (seen.TryDequeue(out _)) { }
        float wasVolume = original.AudioEndpointVolume.MasterVolumeLevelScalar;
        for (int i = 0; i < 6; i++)
        {
            original.AudioEndpointVolume.MasterVolumeLevelScalar = 0.45f + i * 0.03f;
            Thread.Sleep(120);
        }
        original.AudioEndpointVolume.MasterVolumeLevelScalar = wasVolume;
        Thread.Sleep(900);
        Check("six volume changes asked for no rebuild", seen.IsEmpty, $"({seen.Count} events)");
        Console.WriteLine($"        volume put back to {wasVolume * 100:F0}%");

        Console.WriteLine();
        Console.WriteLine("=== a sample rate change must not rebuild twice ===");
        // Mirroring already restarts itself when an endpoint's format changes. If the
        // watcher fired as well, the session would be torn down and rebuilt twice over.
        while (seen.TryDequeue(out _)) { }
        int startRate = original.AudioClient.MixFormat.SampleRate;
        int target = startRate == 48000 ? 44100 : 48000;
        bool rateAccepted = SystemAudioConfig.SetSampleRate(original, target);
        Thread.Sleep(1800);
        Console.WriteLine($"  {startRate} Hz -> {target} Hz accepted: {rateAccepted}");
        Check("format change asked for no rebuild", seen.IsEmpty, $"({seen.Count} events)");
        if (rateAccepted)
        {
            SystemAudioConfig.SetSampleRate(original, startRate);
            Thread.Sleep(1200);
            Check("sample rate put back",
                  original.AudioClient.MixFormat.SampleRate == startRate,
                  $"({original.AudioClient.MixFormat.SampleRate} Hz)");
        }

        Console.WriteLine();
        Console.WriteLine("=== unregistering must be complete ===");
        // Counted rather than inferred: an unregister that quietly failed looks exactly
        // like a quiet machine, and only the count tells the two apart.
        int callbacksAtDispose = watcher.RawNotifications;
        watcher.Dispose();
        while (seen.TryDequeue(out _)) { }
        SystemAudioConfig.SetSampleRate(original, target);
        Thread.Sleep(1500);
        int callbacksAfter = watcher.RawNotifications;
        int eventsAfter = seen.Count;
        SystemAudioConfig.SetSampleRate(original, startRate);
        Thread.Sleep(1200);

        Check("no events after Dispose", eventsAfter == 0, $"({eventsAfter})");
        Check("no callbacks at all after Dispose", callbacksAfter == callbacksAtDispose,
              $"({callbacksAfter - callbacksAtDispose} arrived after unregistering)");
        Check("sample rate put back again",
              original.AudioClient.MixFormat.SampleRate == startRate,
              $"({original.AudioClient.MixFormat.SampleRate} Hz)");

        Console.WriteLine();
        Console.WriteLine($"=== WATCHER: {pass} passed, {fail} failed, {skip} skipped ===");
        return fail == 0 ? 0 : 1;
    }
}
