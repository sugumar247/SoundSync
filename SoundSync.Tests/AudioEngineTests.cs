using Moq;
using SoundSync.Services;
using SoundSync.Services.Providers;
using NAudio.Wave;
using System;
using Xunit;

namespace SoundSync.Tests
{
    public class AudioEngineTests
    {
        private class MockSampleProvider : ISampleProvider
        {
            private readonly float[] samples;
            private int position;

            public MockSampleProvider(WaveFormat waveFormat, float[] samples)
            {
                WaveFormat = waveFormat;
                this.samples = samples;
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                int samplesToRead = Math.Min(count, samples.Length - position);
                Array.Copy(samples, position, buffer, offset, samplesToRead);
                position += samplesToRead;
                return samplesToRead;
            }
        }

        [Fact]
        public void AudioEngine_ReportsConnectionCorrectly()
        {
            var mockEngine = new Mock<IAudioEngine>();
            mockEngine.SetupGet(e => e.IsConnected).Returns(true);
            Assert.True(mockEngine.Object.IsConnected);
        }

        [Fact]
        public void EqualizerSampleProvider_SetProperties()
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            var source = new MockSampleProvider(format, new float[12]);
            var eq = new EqualizerSampleProvider(source);

            eq.BassDb = 5.0f;
            eq.MidDb = -2.0f;
            eq.TrebleDb = 3.0f;

            Assert.Equal(5.0f, eq.BassDb);
            Assert.Equal(-2.0f, eq.MidDb);
            Assert.Equal(3.0f, eq.TrebleDb);

            float[] buffer = new float[12];
            int read = eq.Read(buffer, 0, 12);
            Assert.Equal(12, read);
        }

        [Fact]
        public void DelaySampleProvider_AppliesDelayCorrectly()
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(1000, 1); // 1000 Hz, 1 channel -> 1 sample per ms
            var input = new float[] { 1.0f, 2.0f, 3.0f };
            var source = new MockSampleProvider(format, input);
            var delay = new DelaySampleProvider(source);

            delay.DelayMilliseconds = 2; // Delay by 2 samples

            float[] buffer = new float[3];
            int read = delay.Read(buffer, 0, 3);

            Assert.Equal(3, read);
            Assert.Equal(0f, buffer[0]);
            Assert.Equal(0f, buffer[1]);
            Assert.Equal(1.0f, buffer[2]);
        }

        [Fact]
        public void MeteringSampleProvider_InvokesPeakCallback()
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
            var input = new float[] { 0.5f, -0.8f, 0.2f };
            var source = new MockSampleProvider(format, input);

            float lastPeak = -1f;
            var meter = new MeteringSampleProvider(source, (peak) => lastPeak = peak);

            float[] buffer = new float[3];
            int read = meter.Read(buffer, 0, 3);

            Assert.Equal(3, read);
            Assert.True(lastPeak > 0f);
            Assert.Equal(0.8f, lastPeak);
        }
    }
}
