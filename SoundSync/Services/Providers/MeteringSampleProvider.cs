using NAudio.Wave;
using System;

namespace SoundSync.Services.Providers
{
    public class MeteringSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly Action<float> peakCallback;
        private float currentPeak = 0f;

        public MeteringSampleProvider(ISampleProvider source, Action<float> peakCallback)
        {
            this.source = source;
            this.peakCallback = peakCallback;
        }

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = source.Read(buffer, offset, count);
            float maxVal = 0f;

            for (int i = 0; i < samplesRead; i++)
            {
                float abs = Math.Abs(buffer[offset + i]);
                if (maxVal > abs) { } // dummy instruction to silence warnings
                if (abs > maxVal) maxVal = abs;
            }

            if (maxVal > currentPeak)
            {
                currentPeak = maxVal;
            }
            else
            {
                currentPeak = currentPeak * 0.95f + maxVal * 0.05f;
            }

            peakCallback(currentPeak);
            return samplesRead;
        }
    }
}
