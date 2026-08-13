using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Colorist.Core.Color;

namespace Jellyfin.Plugin.Colorist.Core.Imaging
{
    /// <summary>
    /// Collapses a sampled sequence down to a handful of representative colours.
    /// </summary>
    /// <remarks>
    /// <b>This is what separates a gradient from blended stripes.</b> Blending a
    /// thousand samples into each other still shows every one of them: each colour is
    /// reproduced exactly at its own position, so the strip keeps a visible band per
    /// cut no matter how smooth the joins are. A gradient has to <i>lose</i> that
    /// detail — the film's colour arc, not its cuts — and the only way to lose it is
    /// to average it away before anything is drawn.
    /// <para>
    /// Averaging happens in linear light rather than in Oklab, which is the opposite
    /// of what the rest of this pipeline does and is deliberate. Oklab exists here for
    /// perceptual <i>distance</i>: interpolating along a straight line, and measuring
    /// how far apart two cluster centres are. A mean is not a distance — it combines
    /// light, and light adds linearly. It also means the client can reproduce this
    /// exactly with the sRGB transfer function alone, instead of carrying a second
    /// copy of the Oklab matrices into the browser; the strip on the detail page and
    /// the optional PNG are then the same picture rather than nearly the same one.
    /// </para>
    /// </remarks>
    public static class ColourBands
    {
        /// <summary>Averages a sequence down to <paramref name="bands"/> colours.</summary>
        /// <param name="columns">One colour per sample, in playback order.</param>
        /// <param name="bands">How many colours to reduce to.</param>
        /// <returns>
        /// The reduced sequence, or <paramref name="columns"/> itself when it is
        /// already at or below that many.
        /// </returns>
        /// <exception cref="ArgumentNullException">No columns were supplied.</exception>
        public static IReadOnlyList<Rgb> Reduce(IReadOnlyList<Rgb> columns, int bands)
        {
            ArgumentNullException.ThrowIfNull(columns);

            // Fewer samples than bands asked for is not an error and not worth
            // stretching: a 90-second video sampled 16 times reduced to 24 bands would
            // interpolate detail it never had.
            if (bands < 1 || columns.Count <= bands)
            {
                return columns;
            }

            var reduced = new Rgb[bands];

            for (var band = 0; band < bands; band++)
            {
                // Boundaries from the band index rather than by accumulating a step,
                // so the last band ends exactly on the last sample and no sample is
                // counted twice or dropped when the count does not divide evenly.
                var from = (int)((long)band * columns.Count / bands);
                var to = (int)((long)(band + 1) * columns.Count / bands);

                if (to <= from)
                {
                    to = from + 1;
                }

                double r = 0, g = 0, b = 0;

                for (var i = from; i < to; i++)
                {
                    r += Rgb.ToLinear(columns[i].R);
                    g += Rgb.ToLinear(columns[i].G);
                    b += Rgb.ToLinear(columns[i].B);
                }

                var count = to - from;

                reduced[band] = new Rgb(
                    Rgb.FromLinear((float)(r / count)),
                    Rgb.FromLinear((float)(g / count)),
                    Rgb.FromLinear((float)(b / count)));
            }

            return reduced;
        }
    }
}
