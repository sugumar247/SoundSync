namespace SoundSync.Services
{
    public interface INetworkStreamer
    {
        event System.Action? ClientsChanged;
        event System.Action<string>? CommandReceived;
        void SendToAll(string json);
        System.Collections.Generic.List<SoundSync.Models.LinkClient> GetClients();
        bool IsRunning { get; }
        void Start(int sampleRate, int channels, int port);
        void BroadcastAudio(byte[] buffer, int count);
        void Stop();
    }
}
