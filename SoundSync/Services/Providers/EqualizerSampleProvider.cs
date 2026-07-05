using NAudio.Wave;
using NAudio.Dsp;
using System;

namespace SoundSync.Services.Providers
{
    public class EqualizerSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly BiQuadFilter[] filters;
        private float bassDb;
        private float midDb;
        private float trebleDb;
        private readonly object lockObject = new object();
        private bool filtersNeedUpdate = true;

        public EqualizerSampleProvider(ISampleProvider source)
        {
            this.source = source;
            filters = new BiQuadFilter[source.WaveFormat.Channels * 3];
            CreateFilters();
        }

        public WaveFormat WaveFormat => source.WaveFormat;

        public float BassDb
        {
            get => bassDb;
            set { lock (lockObject) { bassDb = value; filtersNeedUpdate = true; } }
        }

        public float MidDb
        {
            get => midDb;
            set { lock (lockObject) { midDb = value; filtersNeedUpdate = true; } }
        }

        public float TrebleDb
        {
            get => trebleDb;
            set { lock (lockObject) { trebleDb = value; filtersNeedUpdate = true; } }
        }

        private void CreateFilters()
        {
            int channels = WaveFormat.Channels;
            int sampleRate = WaveFormat.SampleRate;

            for (int c = 0; c < channels; c++)
            {
                filters[c * 3] = BiQuadFilter.LowShelf(sampleRate, 200, 1.0f, bassDb);
                filters[c * 3 + 1] = BiQuadFilter.PeakingEQ(sampleRate, 1000, 1.0f, midDb);
                filters[c * 3 + 2] = BiQuadFilter.HighShelf(sampleRate, 5000, 1.0f, trebleDb);
            }
            filtersNeedUpdate = false;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            lock (lockObject)
            {
                if (filtersNeedUpdate)
                {
                    CreateFilters();
                }

                int channels = WaveFormat.Channels;
                for (int i = 0; i < read; i++)
                {
                    int channel = i % channels;
                    float sample = buffer[offset + i];

                    sample = filters[channel * 3].Transform(sample);
                    sample = filters[channel * 3 + 1].Transform(sample);
                    sample = filters[channel * 3 + 2].Transform(sample);

                    buffer[offset + i] = sample;
                }
            }
            return read;
        }
    }
}
