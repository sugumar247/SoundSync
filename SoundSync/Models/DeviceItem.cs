using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SoundSync;
using SoundSync.Services.Providers;


namespace SoundSync.Models
{
    public class DeviceItem : INotifyPropertyChanged
    {
        public MMDevice Device { get; set; } = null!;
        public string Name => Device.FriendlyName;
        public bool IsDefaultDevice { get; set; }

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
                DelayChangedCallback?.Invoke();
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

        // Action callback to trigger delay updates without direct MainWindow coupling
        public Action? DelayChangedCallback { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
