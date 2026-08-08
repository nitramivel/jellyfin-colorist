using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Colorist.Core.Color;

namespace Jellyfin.Plugin.Colorist.Core.Sampling
{
    /// <summary>One analysed frame: when it was, and what colour it reduced to.</summary>
    /// <param name="Seconds">Offset from the start of the sampled window.</param>
    /// <param name="Colour">The frame's representative colour.</param>
    public readonly record struct TimedSample(double Seconds, Rgb Colour);

    /// <summary>
    /// Lays timed samples out along the strip.
    /// </summary>
    /// <remarks>
    /// Needed because keyframe sampling does not produce one frame per stripe. A
    /// three-hour film with a two-second GOP yields around 5,400 keyframes for a
    /// 1,000-stripe strip, so several samples share a stripe; a sparsely-keyed
    /// 22-minute episode may yield 200 for the same request, so there are fewer
    /// samples than stripes. Both have to end up as a strip whose x-axis is
    /// honestly proportional to runtime.
    /// </remarks>
    public static class ColumnBinner
    {
        /// <summary>Bins samples into stripes.</summary>
        /// <param name="samples">Samples in time order.</param>
        /// <param name="requestedColumns">The configured stripe count.</param>
        /// <param name="windowSeconds">Length of the sampled window.</param>
        /// <returns>One colour per stripe.</returns>
        public static IReadOnlyList<Rgb> Bin(
            IReadOnlyList<TimedSample> samples,
            int requestedColumns,
            double windowSeconds)
        {
            if (samples.Count == 0)
            {
                return Array.Empty<Rgb>();
            }

            // Never more stripes than there were frames. Asking for 1,000 columns from
            // 200 keyframes and filling the gaps would claim a temporal resolution the
            // data does not have — the same colour repeated five times reads as five
            // measurements. Fewer, honest stripes stretch to the same output width.
            var columns = Math.Max(1, Math.Min(requestedColumns, samples.Count));

            if (windowSeconds <= 0)
            {
                return Uniform(samples, columns);
            }

            var accumulator = new (double L, double A, double B, int Count)[columns];

            foreach (var sample in samples)
            {
                var position = sample.Seconds / windowSeconds;
                var index = Math.Clamp((int)(position * columns), 0, columns - 1);

                var lab = sample.Colour.ToOklab();
                ref var slot = ref accumulator[index];
                slot.L += lab.L;
                slot.A += lab.A;
                slot.B += lab.B;
                slot.Count++;
            }

            var result = new Rgb[columns];
            var lastFilled = -1;

            for (var i = 0; i < columns; i++)
            {
                var slot = accumulator[i];

                if (slot.Count > 0)
                {
                    // Averaging within a stripe is safe in a way that averaging within
                    // a frame is not: these are already one-colour-per-frame answers
                    // from adjacent moments of the same shot, so there is no vivid
                    // detail left to cancel out. Done in Oklab so a stripe spanning a
                    // cut blends perceptually rather than through grey.
                    result[i] = new Oklab(
                        (float)(slot.L / slot.Count),
                        (float)(slot.A / slot.Count),
                        (float)(slot.B / slot.Count)).ToSrgb();

                    lastFilled = i;
                }
                else
                {
                    // A gap means the encoder placed no keyframe in this interval —
                    // usually a long static shot, where holding the previous colour is
                    // not merely a repair but the correct reading of the footage.
                    result[i] = lastFilled >= 0 ? result[lastFilled] : samples[0].Colour;
                }
            }

            return result;
        }

        private static IReadOnlyList<Rgb> Uniform(IReadOnlyList<TimedSample> samples, int columns)
        {
            var result = new Rgb[columns];

            for (var i = 0; i < columns; i++)
            {
                result[i] = samples[Math.Min(samples.Count - 1, i * samples.Count / columns)].Colour;
            }

            return result;
        }
    }
}
