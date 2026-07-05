using SoundSync.Models;
using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;

namespace SoundSync.Services
{
    public interface IAudioEngine
    {
        List<MMDevice> GetActiveRenderDevices();
        void Connect(List<DeviceItem> selectedDevices, INetworkStreamer networkStreamer, Action<string> logCallback, Action onDisconnectedCallback);
        void Disconnect(List<DeviceItem> activeDevices);
        void UpdateRelativeDelays(List<DeviceItem> activeDevices);
        bool IsConnected { get; }
    }
}
