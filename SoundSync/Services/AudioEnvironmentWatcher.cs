using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SoundSync.Services
{
    /// <summary>Why the audio setup changed under the app's feet.</summary>
    public enum AudioEnvironmentChange
    {
        /// <summary>A device appeared, disappeared, or was enabled or disabled.</summary>
        DeviceSet,

        /// <summary>Windows started sending audio somewhere else.</summary>
        DefaultDevice,

        /// <summary>Someone connected or disconnected over Remote Desktop.</summary>
        RemoteSession,

        /// <summary>The console session was connected or disconnected.</summary>
        ConsoleSession
    }

    /// <summary>
    /// Watches for the things that pull the ground out from under a running mirror session.
    ///
    /// Two separate sources, because they report different events:
    ///
    ///  - Windows' own device notifications, for a headset being unplugged, a monitor being
    ///    docked, a driver restarting, or the default output moving.
    ///
    ///  - Session notifications, for Remote Desktop. Connecting over RDP hands the session a
    ///    different set of audio endpoints entirely, and the ones the mirror was holding stop
    ///    existing. Nothing in the audio stack announces that as a device change, so without
    ///    watching sessions the app sits there believing it is still mirroring.
    ///
    /// Reports what happened and lets the caller decide; it never touches the engine itself.
    /// </summary>
    public sealed class AudioEnvironmentWatcher : IDisposable
    {
        private MMDeviceEnumerator? enumerator;
        private readonly NotificationSink sink;
        private bool registered;
        private int rawNotifications;

        /// <summary>
        /// How many notifications Windows has handed this sink, including the ones filtered
        /// out as noise. Zero after something has definitely changed means the registration
        /// did not take, which is otherwise indistinguishable from a quiet machine.
        /// </summary>
        public int RawNotifications => rawNotifications;

        /// <summary>Raised on the thread Windows chose - marshal before touching the UI.</summary>
        public event Action<AudioEnvironmentChange, string>? Changed;

        public AudioEnvironmentWatcher()
        {
            sink = new NotificationSink(this);
        }

        public void Start()
        {
            if (registered) return;

            // Registered from a pool thread, never the UI thread, and deliberately so.
            // Windows delivers these notifications into the apartment that registered them,
            // so registering on the UI thread would put the audio service on the other end
            // of the window's message queue - and anything that stops that queue, a message
            // box or a slow redraw, would stall the audio service with it. On a pool thread
            // the callbacks arrive on their own and cannot be held up by the interface.
            try
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    enumerator = new MMDeviceEnumerator();
                    enumerator.RegisterEndpointNotificationCallback(sink);
                }).Wait(5000);
                registered = enumerator != null;
            }
            catch
            {
                // Without this the app still works; it just will not notice a device change.
            }

            try
            {
                Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            }
            catch
            {
                // Session notifications need a message pump; a headless host does without.
            }
        }

        private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case Microsoft.Win32.SessionSwitchReason.RemoteConnect:
                    Raise(AudioEnvironmentChange.RemoteSession,
                          "A Remote Desktop session connected, which replaces the audio devices for this session.");
                    break;

                case Microsoft.Win32.SessionSwitchReason.RemoteDisconnect:
                    Raise(AudioEnvironmentChange.RemoteSession,
                          "The Remote Desktop session disconnected, so the local audio devices are back.");
                    break;

                case Microsoft.Win32.SessionSwitchReason.ConsoleConnect:
                    Raise(AudioEnvironmentChange.ConsoleSession,
                          "The console session reconnected.");
                    break;

                case Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect:
                    Raise(AudioEnvironmentChange.ConsoleSession,
                          "The console session disconnected.");
                    break;
            }
        }

        private void Count() => Interlocked.Increment(ref rawNotifications);

        private void Raise(AudioEnvironmentChange kind, string reason) => Changed?.Invoke(kind, reason);

        /// <summary>Receives Windows' endpoint notifications and forwards the interesting ones.</summary>
        private sealed class NotificationSink : IMMNotificationClient
        {
            private readonly AudioEnvironmentWatcher owner;
            public NotificationSink(AudioEnvironmentWatcher owner) { this.owner = owner; }

            public void OnDeviceStateChanged(string deviceId, DeviceState newState)
            {
                owner.Count();
                owner.Raise(AudioEnvironmentChange.DeviceSet,
                            $"An output changed state ({newState}).");
            }

            public void OnDeviceAdded(string deviceId)
            {
                owner.Count();
                owner.Raise(AudioEnvironmentChange.DeviceSet, "An output appeared.");
            }

            public void OnDeviceRemoved(string deviceId)
            {
                owner.Count();
                owner.Raise(AudioEnvironmentChange.DeviceSet, "An output was removed.");
            }

            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            {
                owner.Count();

                // Only the playback default matters here, and only the role the engine uses.
                if (flow != DataFlow.Render || role != Role.Multimedia) return;
                owner.Raise(AudioEnvironmentChange.DefaultDevice,
                            "Windows is sending audio to a different device now.");
            }

            public void OnPropertyValueChanged(string deviceId, PropertyKey key)
            {
                // Far too chatty to act on: volume alone raises this constantly. Counted
                // only, so a test can tell a working registration from a quiet machine.
                owner.Count();
            }
        }

        public void Dispose()
        {
            try { Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }

            if (registered)
            {
                // Off the UI thread as well, to match where it was registered.
                try
                {
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { enumerator?.UnregisterEndpointNotificationCallback(sink); } catch { }
                        try { enumerator?.Dispose(); } catch { }
                        enumerator = null;
                    }).Wait(5000);
                }
                catch { }
                registered = false;
            }
        }
    }
}
