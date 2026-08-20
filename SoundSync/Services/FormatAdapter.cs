using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundSync.Services
{
    /// <summary>
    /// Converts a captured stream into exactly the format an output endpoint expects.
    ///
    /// WASAPI in shared mode will resample between rates on its own, but only for simple
    /// layouts, and it never changes the channel count. Handing it anything else fails with
    /// a bare "Value does not fall within the expected range". Doing the whole conversion
    /// here, and handing WASAPI its own mix format, removes that class of failure.
    ///
    /// Order matters: resample while the stream still has its source channel count, then
    /// map channels last. Resampling four channels is what the built-in resamplers choke on.
    /// </summary>
    public static class FormatAdapter
    {
        /// <summary>
        /// Wraps <paramref name="source"/> so it comes out at <paramref name="target"/>'s
        /// sample rate and channel count. Returns the source untouched when it already matches.
        /// </summary>
        public static ISampleProvider Adapt(ISampleProvider source, WaveFormat target)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) return source;

            ISampleProvider result = source;

            // 1. Sample rate, still at the source channel count.
            if (result.WaveFormat.SampleRate != target.SampleRate)
            {
                result = new WdlResamplingSampleProvider(result, target.SampleRate);
            }

            // 2. Channel layout last.
            if (result.WaveFormat.Channels != target.Channels)
            {
                result = MapChannels(result, target.Channels);
            }

            return result;
        }

        /// <summary>
        /// Adapts the stream and hands back a byte provider tagged with the endpoint's own
        /// format.
        ///
        /// WASAPI requires WAVEFORMATEXTENSIBLE for anything above two channels, and NAudio's
        /// sample providers describe themselves with a plain WAVEFORMATEX. The bytes are
        /// identical 32-bit float either way, so the stream only has to be relabelled - but
        /// without that relabelling every stereo-to-quad output is refused outright.
        /// </summary>
        public static IWaveProvider AdaptToWaveProvider(ISampleProvider source, WaveFormat target)
        {
            IWaveProvider provider = Adapt(source, target).ToWaveProvider();

            bool sameShape = target != null
                             && provider.WaveFormat.SampleRate == target.SampleRate
                             && provider.WaveFormat.Channels == target.Channels
                             && provider.WaveFormat.BitsPerSample == target.BitsPerSample
                             && provider.WaveFormat.Encoding != target.Encoding;

            return sameShape ? new RelabelledWaveProvider(provider, target!) : provider;
        }

        /// <summary>
        /// Passes bytes straight through while reporting a different, byte-compatible
        /// WaveFormat. Only used when both formats describe the same samples.
        /// </summary>
        private sealed class RelabelledWaveProvider : IWaveProvider
        {
            private readonly IWaveProvider source;

            public RelabelledWaveProvider(IWaveProvider source, WaveFormat format)
            {
                this.source = source;
                WaveFormat = format;
            }

            public WaveFormat WaveFormat { get; }

            public int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        }

        /// <summary>
        /// Fans a stream out to, or folds it down to, <paramref name="targetChannels"/>.
        /// Stereo to quad duplicates the pair; quad to stereo keeps the front pair.
        /// </summary>
        private static ISampleProvider MapChannels(ISampleProvider source, int targetChannels)
        {
            if (source.WaveFormat.Channels == targetChannels) return source;
            return new Providers.ChannelMapSampleProvider(source, targetChannels);
        }
    }
}
