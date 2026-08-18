using System;
using NAudio.Wave;

namespace SoundSync.Services.Providers
{
    /// <summary>
    /// Converts between channel layouts without losing anything.
    ///
    /// Folding down (4 to 2, 6 to 2): every source channel is averaged into a destination
    /// channel rather than dropped, so audio that only exists on the rear or centre
    /// channels still reaches a stereo speaker pair.
    ///
    /// Fanning out (2 to 4, 2 to 6): the source channels repeat across the destination, so
    /// a stereo source fills every speaker instead of leaving half of them silent.
    /// </summary>
    public class ChannelMapSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly int sourceChannels;
        private readonly int targetChannels;

        /// <summary>For each destination channel, which source channels feed it.</summary>
        private readonly int[][] contributors;

        private float[] sourceBuffer = Array.Empty<float>();

        public ChannelMapSampleProvider(ISampleProvider source, int targetChannels)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            if (targetChannels < 1) throw new ArgumentOutOfRangeException(nameof(targetChannels));

            sourceChannels = source.WaveFormat.Channels;
            this.targetChannels = targetChannels;

            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, targetChannels);
            contributors = BuildMap(sourceChannels, targetChannels);
        }

        public WaveFormat WaveFormat { get; }

        /// <summary>
        /// Spreads source channels evenly over the destination ones. Source channel i always
        /// lands on destination i % targetChannels, so nothing is ever dropped, and every
        /// destination channel gets at least one source when fanning out.
        /// </summary>
        private static int[][] BuildMap(int sourceChannels, int targetChannels)
        {
            var lists = new System.Collections.Generic.List<int>[targetChannels];
            for (int i = 0; i < targetChannels; i++) lists[i] = new System.Collections.Generic.List<int>();

            if (sourceChannels >= targetChannels)
            {
                // Fold down: channel 0 and 2 both feed destination 0 when going 4 to 2.
                for (int s = 0; s < sourceChannels; s++) lists[s % targetChannels].Add(s);
            }
            else
            {
                // Fan out: destination 2 repeats source 0 when going 2 to 4.
                for (int t = 0; t < targetChannels; t++) lists[t].Add(t % sourceChannels);
            }

            var map = new int[targetChannels][];
            for (int i = 0; i < targetChannels; i++) map[i] = lists[i].ToArray();
            return map;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / targetChannels;
            if (frames == 0) return 0;

            int needed = frames * sourceChannels;
            if (sourceBuffer.Length < needed) sourceBuffer = new float[needed];

            int readSamples = source.Read(sourceBuffer, 0, needed);
            int readFrames = readSamples / sourceChannels;

            for (int frame = 0; frame < readFrames; frame++)
            {
                int sourceBase = frame * sourceChannels;
                int targetBase = offset + frame * targetChannels;

                for (int channel = 0; channel < targetChannels; channel++)
                {
                    int[] from = contributors[channel];
                    float sum = 0f;
                    for (int i = 0; i < from.Length; i++) sum += sourceBuffer[sourceBase + from[i]];
                    buffer[targetBase + channel] = sum / from.Length;
                }
            }

            return readFrames * targetChannels;
        }
    }
}
