using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Colorist.Core.Color;

namespace Jellyfin.Plugin.Colorist.Core.Imaging
{
    /// <summary>
    /// Turns the per-sample colours into the finished pixel buffer.
    /// </summary>
    public static class BarcodeComposer
    {
        /// <summary>Composes the strip.</summary>
        /// <param name="columns">One colour per sample, in playback order.</param>
        /// <param name="width">Output width in pixels.</param>
        /// <param name="height">Output height in pixels.</param>
        /// <param name="smooth">Whether to blend between adjacent samples rather than banding.</param>
        /// <param name="bands">
        /// When positive, the samples are averaged down to this many colours first, so
        /// the result is one gradient across the whole strip rather than every sample
        /// blended into its neighbour. Only meaningful with <paramref name="smooth"/>:
        /// reducing and then banding would draw a few wide blocks, which is the one
        /// combination nothing asks for, so it is ignored there.
        /// </param>
        /// <returns>A tightly-packed rgb24 buffer of width × height × 3 bytes.</returns>
        /// <exception cref="ArgumentException">No columns were supplied.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Dimensions are not positive.</exception>
        public static byte[] Compose(
            IReadOnlyList<Rgb> columns,
            int width,
            int height,
            bool smooth,
            int bands = 0)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

            if (columns.Count == 0)
            {
                throw new ArgumentException("A barcode needs at least one sample.", nameof(columns));
            }

            if (smooth && bands > 0)
            {
                columns = ColourBands.Reduce(columns, bands);
            }

            var stride = width * 3;
            var row = new byte[stride];

            for (var x = 0; x < width; x++)
            {
                var colour = smooth
                    ? Interpolated(columns, x, width)
                    : columns[NearestIndex(columns.Count, x, width)];

                row[(x * 3) + 0] = colour.R;
                row[(x * 3) + 1] = colour.G;
                row[(x * 3) + 2] = colour.B;
            }

            // Every row of a vertical-stripe barcode is the same row. Building it once
            // and blitting turns an O(width × height) colour computation into O(width)
            // plus a memory copy, which at a 1920×360 output is the difference between
            // seven hundred thousand interpolations per item and nineteen hundred.
            var buffer = new byte[stride * height];

            for (var y = 0; y < height; y++)
            {
                row.CopyTo(buffer, y * stride);
            }

            return buffer;
        }

        private static int NearestIndex(int count, int x, int width) =>
            Math.Min(count - 1, (int)((long)x * count / width));

        /// <summary>
        /// Blends between the two samples either side of this pixel, in Oklab.
        /// </summary>
        /// <remarks>
        /// Interpolating in sRGB is the obvious thing and it is wrong in a way that
        /// shows: the midpoint between two vivid colours of different hue passes
        /// through a darker, greyer place than either endpoint, so a smoothed strip
        /// picks up a dark seam at every transition. Oklab is built so that a
        /// straight line between two colours looks like a straight line, which is
        /// exactly the property a gradient needs.
        /// </remarks>
        private static Rgb Interpolated(IReadOnlyList<Rgb> columns, int x, int width)
        {
            if (columns.Count == 1)
            {
                return columns[0];
            }

            // Sampled at the centre of the pixel so the first and last samples land
            // fully at the two ends rather than half a pixel inside them.
            var position = ((x + 0.5) / width * columns.Count) - 0.5;
            var lower = (int)Math.Floor(position);
            var t = (float)(position - lower);

            var a = columns[Math.Clamp(lower, 0, columns.Count - 1)].ToOklab();
            var b = columns[Math.Clamp(lower + 1, 0, columns.Count - 1)].ToOklab();

            return new Oklab(
                a.L + ((b.L - a.L) * t),
                a.A + ((b.A - a.A) * t),
                a.B + ((b.B - a.B) * t)).ToSrgb();
        }
    }
}
