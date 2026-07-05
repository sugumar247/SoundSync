using NAudio.Wave;
using System.Collections.Generic;

namespace SoundSync.Services.Providers
{
    public class DelaySampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly Queue<float> delayQueue = new Queue<float>();
        private readonly object lockObject = new object();
        private int delaySamples;
        private int currentDelayMs;

        public DelaySampleProvider(ISampleProvider source)
        {
            this.source = source;
        }

        public WaveFormat WaveFormat => source.WaveFormat;

        public int DelayMilliseconds
        {
            get => currentDelayMs;
            set
            {
                lock (lockObject)
                {
                    currentDelayMs = value;
                    delaySamples = (WaveFormat.SampleRate * WaveFormat.Channels * value) / 1000;

                    // Trim down items if the delay size decreases
                    while (delayQueue.Count > delaySamples)
                    {
                        delayQueue.Dequeue();
                    }
                }
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            // Read input samples from the preceding pipeline source first
            int read = source.Read(buffer, offset, count);

            lock (lockObject)
            {
                if (delaySamples <= 0)
                {
                    delayQueue.Clear();
                    return read;
                }

                for (int i = 0; i < read; i++)
                {
                    float incomingSample = buffer[offset + i];

                    // Populate queue with zero silence until the requested millisecond delay distance is filled
                    if (delayQueue.Count < delaySamples)
                    {
                        delayQueue.Enqueue(incomingSample);
                        buffer[offset + i] = 0f; // Return silence during initial delay loading phase
                    }
                    else
                    {
                        // Extract oldest sample and inject newest sample simultaneously
                        buffer[offset + i] = delayQueue.Dequeue();
                        delayQueue.Enqueue(incomingSample);
                    }
                }

                return read;
            }
        }
    }
}
