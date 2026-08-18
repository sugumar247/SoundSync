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
        void ApplyMakeUpGain(List<DeviceItem> activeDevices, float defaultDeviceVolume, bool enabled);
        void UpdateRelativeDelays(List<DeviceItem> activeDevices);
        void SetDefaultDeviceVolume(float volume);
        float SourcePeakLevel { get; }
        bool IsConnected { get; }
    }
}
