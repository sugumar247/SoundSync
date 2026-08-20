using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
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

        private bool applyingSystemVolume;
        private bool applyingSampleRate;

        /// <summary>Sample rates this endpoint's hardware reports as playable.</summary>
        public List<int> SupportedRates { get; private set; } = new List<int>();

        /// <summary>
        /// Windows' shared-mode sample rate for this endpoint - the "Default Format" of
        /// the Sound control panel. Setting it changes the real system configuration.
        /// </summary>
        public int SampleRate
        {
            get => _sampleRate;
            set
            {
                if (applyingSampleRate || value <= 0 || value == _sampleRate) return;
                applyingSampleRate = true;
                try
                {
                    if (Services.SystemAudioConfig.SetSampleRate(Device, value))
                    {
                        _sampleRate = value;
                        SampleRateStatus = string.Empty;
                        SampleRateTooltip = RateTooltipBase;
                        FormatChangedCallback?.Invoke(this);
                    }
                    else
                    {
                        // Snap back to what Windows actually has. Leaving the rejected value
                        // on screen would claim a change that never happened.
                        _sampleRate = Services.SystemAudioConfig.GetSampleRate(Device);
                        SampleRateStatus = "refused";
                        SampleRateTooltip = Services.AudioErrors.ExplainRateRejection(Device, value);
                    }
                }
                finally
                {
                    applyingSampleRate = false;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SampleRateStatus));
                    OnPropertyChanged(nameof(SampleRateTooltip));
                }
            }
        }
        private int _sampleRate;

        /// <summary>Empty when the last rate change succeeded, otherwise a short reason.</summary>
        private string _sampleRateStatus = string.Empty;
        public string SampleRateStatus
        {
            get => _sampleRateStatus;
            private set { _sampleRateStatus = value; OnPropertyChanged(); }
        }

        private const string RateTooltipBase =
            "Windows sample rate for this output - the same setting as Sound panel > Properties > " +
            "Advanced > Default Format.\n\nThe list shows what this hardware reports it can play. " +
            "Matching this to the source device's rate removes the real-time resampling step, " +
            "which lowers both delay and CPU use.\n\nA device that is playing right now will usually " +
            "refuse to switch.";

        private string _sampleRateTooltip = RateTooltipBase;
        /// <summary>Explains the picker, or why the last change was refused.</summary>
        public string SampleRateTooltip
        {
            get => _sampleRateTooltip;
            private set { _sampleRateTooltip = value; OnPropertyChanged(); }
        }

        private bool _isHidden;
        /// <summary>Hidden rows stay configured but are filtered out of the list.</summary>
        public bool IsHidden
        {
            get => _isHidden;
            set { _isHidden = value; OnPropertyChanged(); }
        }

        /// <summary>Badge text shown beside the name: SOURCE for the current default.</summary>
        public string DefaultBadge => IsDefaultDevice ? "SYSTEM DEFAULT" : string.Empty;

        /// <summary>
        /// False for the system default device. Its audio is what gets captured and it plays
        /// straight from Windows, so it never passes through the delay or equaliser stages -
        /// those controls would move without changing a thing.
        /// </summary>
        public bool CanProcessAudio => !IsDefaultDevice;

        private bool _autoAlignDelay;
        /// <summary>
        /// Let the app keep this output's delay matched to the slowest mirrored output,
        /// rechecked every two seconds.
        /// </summary>
        public bool AutoAlignDelay
        {
            get => _autoAlignDelay;
            set { _autoAlignDelay = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanEditDelay)); }
        }

        /// <summary>The slider is for hand tuning, so it locks while the app is driving it.</summary>
        public bool CanEditDelay => CanProcessAudio && !AutoAlignDelay;

        private bool _syncVolumeWithDefault;
        /// <summary>Follow the default device's Windows volume, keeping the ratio below.</summary>
        public bool SyncVolumeWithDefault
        {
            get => _syncVolumeWithDefault;
            set { _syncVolumeWithDefault = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// This output's volume divided by the default device's, captured when the sync was
        /// switched on. Keeping the ratio preserves the balance you had set up rather than
        /// slamming every speaker to the same number.
        /// </summary>
        public float VolumeRatioToDefault { get; set; } = 1.0f;

        /// <summary>Sync options only make sense for a ticked, non-default output.</summary>
        public bool ShowSyncOptions => !IsDefaultDevice && IsSelected;

        // ---- how this endpoint is wired up ------------------------------------------
        //
        // Windows already knows, and hands it over in the endpoint property store, so
        // reading it costs one property lookup at load time.

        /// <summary>PKEY_AudioEndpoint_FormFactor.</summary>
        private static readonly PropertyKey FormFactorKey =
            new PropertyKey(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

        /// <summary>PKEY_Device_EnumeratorName - "HDAUDIO", "USB", "ROOT" for virtual ones.</summary>
        private static readonly PropertyKey EnumeratorKey =
            new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

        private string _connectionBadge = string.Empty;
        /// <summary>Short label describing how this output is connected.</summary>
        public string ConnectionBadge
        {
            get => _connectionBadge;
            private set { _connectionBadge = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasConnectionBadge)); }
        }

        public bool HasConnectionBadge => ConnectionBadge.Length > 0;

        private string _connectionTooltip = string.Empty;
        public string ConnectionTooltip
        {
            get => _connectionTooltip;
            private set { _connectionTooltip = value; OnPropertyChanged(); }
        }

        /// <summary>Reads the connection type Windows reports for this endpoint.</summary>
        public void LoadConnectionKind()
        {
            int formFactor = -1;
            string enumerator = string.Empty;
            try
            {
                var props = Device.Properties;
                if (props.Contains(FormFactorKey)) formFactor = Convert.ToInt32(props[FormFactorKey].Value);
                if (props.Contains(EnumeratorKey)) enumerator = Convert.ToString(props[EnumeratorKey].Value) ?? string.Empty;
            }
            catch { }

            // EndpointFormFactor: 7 UnknownDigitalPassthrough, 8 SPDIF, 9 DigitalAudioDisplayDevice
            if (formFactor == 9)
            {
                ConnectionBadge = "HDMI DIGITAL - SLOWER";
                ConnectionTooltip =
                    "Windows reports this endpoint as a digital display device: HDMI or DisplayPort." + NewParagraph
                    + "The cable itself is not slow. What is slow is the screen on the far end - televisions and "
                    + "monitors run their own audio processing after the signal leaves the PC, often tens of "
                    + "milliseconds. That part happens outside the computer, so no program can see it, measure "
                    + "it, or subtract it." + NewParagraph
                    + "So this is usually the LAST device in the room to make a sound." + NewParagraph
                    + WhySlowestShouldBeDefault;
            }
            else if (formFactor == 8 || formFactor == 7)
            {
                ConnectionBadge = "DIGITAL PASSTHROUGH";
                ConnectionTooltip =
                    "A digital output such as S/PDIF or optical." + NewParagraph
                    + "Whatever receiver, soundbar or decoder sits on the far end adds its own delay, outside "
                    + "the computer and invisible to any program. Treat it as one of the slower devices." + NewParagraph
                    + WhySlowestShouldBeDefault;
            }
            else if (string.Equals(enumerator, "ROOT", StringComparison.OrdinalIgnoreCase))
            {
                ConnectionBadge = "VIRTUAL";
                ConnectionTooltip =
                    "A virtual device created by software, not real hardware - a streaming or capture driver." + NewParagraph
                    + "There is no speaker on the end of it, so mirroring here feeds another program rather than "
                    + "a room. Timing against it is meaningless: nobody hears it directly.";
            }
            else if (string.Equals(enumerator, "USB", StringComparison.OrdinalIgnoreCase))
            {
                ConnectionBadge = "USB";
                ConnectionTooltip =
                    "A USB audio device. The USB link adds a small, steady delay of its own - a few "
                    + "milliseconds - far less than a television adds, but a little more than a jack on the "
                    + "sound card." + NewParagraph
                    + "Sits in the middle: usually a good mirror, and it can be delayed to match a slower device." + NewParagraph
                    + WhySlowestShouldBeDefault;
            }
            else if (formFactor >= 0)
            {
                ConnectionBadge = "ANALOG - FASTER";
                ConnectionTooltip =
                    "An analogue output on the sound card: the speaker or headphone jack." + NewParagraph
                    + "The signal goes straight out through the card's own converter with nothing downstream to "
                    + "process it, so this is normally the shortest path and the FIRST device to make a sound." + NewParagraph
                    + "That makes it a good mirror - being early, it has room to be delayed until it lines up "
                    + "with a slower device." + NewParagraph
                    + WhySlowestShouldBeDefault;
            }
        }

        /// <summary>Dims the controls that have no effect on the default device.</summary>
        public double ProcessingOpacity => IsDefaultDevice ? 0.35 : 1.0;

        /// <summary>Explains the delay slider, including why it is dead on the default device.</summary>
        public string DelayTooltip => IsDefaultDevice
            ? "Disabled because this is the system default device." + NewParagraph
              + "Its audio is the source being captured and it plays straight from Windows, so SoundSync "
              + "never processes it - delaying it is not possible. Delay the outputs that arrive too soon "
              + "instead, or make a different device the default."
            : "Hold this output back so it lines up with the others, 0 to 500 ms in 1 ms steps." + NewParagraph
              + "Delay can only be added: the default device plays straight from Windows and cannot be "
              + "pulled earlier, so delay the ones that arrive too soon." + NewParagraph
              + "Click the slider first, then the arrow keys move it 1 ms at a time and the mouse wheel "
              + "1 ms per notch.";

        private static readonly string NewParagraph = Environment.NewLine + Environment.NewLine;

        /// <summary>
        /// The one rule that decides which device should be the system default. Repeated in
        /// every connection tooltip because it is the thing people get backwards.
        /// </summary>
        private static readonly string WhySlowestShouldBeDefault =
            "WHY THE SLOWEST DEVICE SHOULD BE THE DEFAULT" + Environment.NewLine
            + "SoundSync captures whatever the default device plays and copies it to the others. "
            + "That audio has already left Windows by then, so the default device can only ever be "
            + "made LATER by adding delay - never earlier." + NewParagraph
            + "So delay is one-way: it can only be added to the mirrors. If the default is the fastest "
            + "device, the slow ones are already behind and there is nothing left to do - you would have "
            + "to delay the default, which is impossible." + NewParagraph
            + "Make the SLOWEST device the default. Then every mirror is early, and each one can be held "
            + "back until it lands together with it.";

        /// <summary>Explains the SYSTEM DEFAULT badge and the rule behind choosing it.</summary>
        public string DefaultBadgeTooltip =>
            "Windows is sending all system audio here right now." + NewParagraph
            + "This is the device SoundSync captures from - the source. It cannot also be a destination, "
            + "so its tick box, delay and equaliser are switched off." + NewParagraph
            + WhySlowestShouldBeDefault;

        /// <summary>Explains the SET AS DEFAULT button, including when you would want it.</summary>
        public string SetAsDefaultTooltip =>
            "Make this the output Windows sends everything to, for all three roles." + NewParagraph
            + "SoundSync captures FROM the default device, so this also changes what gets copied to the "
            + "others. If mirroring is running it stops first - press CONNECT again afterwards." + NewParagraph
            + WhySlowestShouldBeDefault;

        /// <summary>Reads the current rate and the supported list from Windows.</summary>
        public void LoadSystemFormat()
        {
            try
            {
                SupportedRates = Services.SystemAudioConfig.GetSupportedRates(Device);
                applyingSampleRate = true;
                _sampleRate = Services.SystemAudioConfig.GetSampleRate(Device);
                applyingSampleRate = false;
                OnPropertyChanged(nameof(SupportedRates));
                OnPropertyChanged(nameof(SampleRate));
            }
            catch { }
        }

        /// <summary>
        /// Windows volume of this endpoint, 0..1. Writing it moves the real system
        /// slider; Windows changes made elsewhere are pushed back here by
        /// <see cref="AttachSystemVolume"/>, so the two stay in sync.
        /// </summary>
        public float SystemVolume
        {
            get
            {
                try { return Device.AudioEndpointVolume.MasterVolumeLevelScalar; }
                catch { return 0f; }
            }
            set
            {
                if (applyingSystemVolume) return;
                try
                {
                    float clamped = Math.Clamp(value, 0f, 1f);
                    if (Math.Abs(Device.AudioEndpointVolume.MasterVolumeLevelScalar - clamped) < 0.0005f) return;
                    Device.AudioEndpointVolume.MasterVolumeLevelScalar = clamped;
                    if (clamped > 0f && Device.AudioEndpointVolume.Mute)
                        Device.AudioEndpointVolume.Mute = false;
                }
                catch { }
                OnPropertyChanged();
                OnPropertyChanged(nameof(SystemVolumePercent));
            }
        }

        /// <summary>Same value as a percentage, for the readout next to the slider.</summary>
        public string SystemVolumePercent => $"{SystemVolume * 100:F0}%";

        /// <summary>
        /// Mirrors Windows-side volume changes back into the UI. <paramref name="post"/>
        /// marshals to the UI thread - the notification arrives on an audio thread.
        /// </summary>
        public void AttachSystemVolume(Action<Action> post)
        {
            try
            {
                Device.AudioEndpointVolume.OnVolumeNotification += _ => post(() =>
                {
                    applyingSystemVolume = true;
                    OnPropertyChanged(nameof(SystemVolume));
                    OnPropertyChanged(nameof(SystemVolumePercent));
                    applyingSystemVolume = false;
                });
            }
            catch { }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowSyncOptions)); }
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

        /// <summary>Buffer feeding this output, used to measure the real delay.</summary>
        public NAudio.Wave.BufferedWaveProvider? OutputBuffer { get; set; }

        /// <summary>Fixed part of the delay: what was asked of WASAPI, in milliseconds.</summary>
        public int EndpointLatencyMs { get; set; }

        private double _measuredLatencyMs;
        /// <summary>
        /// Delay actually observed on this output: how much audio is waiting in its buffer,
        /// plus the endpoint latency, plus whatever the user dialled in. Measured, not guessed.
        /// </summary>
        public double MeasuredLatencyMs
        {
            get => _measuredLatencyMs;
            private set
            {
                if (Math.Abs(_measuredLatencyMs - value) < 0.5) return;
                _measuredLatencyMs = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MeasuredLatencyText));
            }
        }

        /// <summary>Reading shown next to the delay slider.</summary>
        public string MeasuredLatencyText => OutputBuffer == null ? "--" : $"~{MeasuredLatencyMs:F0} ms";

        /// <summary>Re-reads the buffer occupancy. Called on a timer while connected.</summary>
        public void RefreshMeasuredLatency()
        {
            var buffer = OutputBuffer;
            if (buffer == null) { MeasuredLatencyMs = 0; return; }
            try
            {
                double buffered = buffer.BufferedBytes * 1000.0 / buffer.WaveFormat.AverageBytesPerSecond;
                MeasuredLatencyMs = buffered + EndpointLatencyMs + Math.Max(0, Delay);
            }
            catch { }
        }

        public VolumeSampleProvider? VolumeProvider { get; set; }
        public EqualizerSampleProvider? EqualizerProvider { get; set; }
        public DelaySampleProvider? DelayProvider { get; set; }

        // Action callback to trigger delay updates without direct MainWindow coupling
        public Action? DelayChangedCallback { get; set; }

        /// <summary>
        /// Raised after this endpoint's sample rate changed. Windows tears the audio engine
        /// down for a device whose format changes, which kills a loopback capture reading
        /// from it, so whoever is mirroring needs to restart.
        /// </summary>
        public Action<DeviceItem>? FormatChangedCallback { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
