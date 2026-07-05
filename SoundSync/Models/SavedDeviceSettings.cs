namespace SoundSync.Models
{
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
}
