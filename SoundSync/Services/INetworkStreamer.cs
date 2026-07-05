namespace SoundSync.Services
{
    public interface INetworkStreamer
    {
        bool IsRunning { get; }
        void Start(int sampleRate, int port);
        void BroadcastAudio(byte[] buffer, int count);
        void Stop();
    }
}
