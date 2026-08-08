using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Colorist.Core.Color
{
    /// <summary>One quantised colour bucket: where it sits, and how many pixels landed in it.</summary>
    /// <param name="Lab">The bucket's colour in Oklab, where the clustering happens.</param>
    /// <param name="Rgb">The same colour in sRGB, so a winning bucket needs no conversion back.</param>
    /// <param name="Count">How many source pixels fell into the bucket.</param>
    internal readonly record struct ColorBucket(Oklab Lab, Rgb Rgb, int Count);

    /// <summary>
    /// Reduces a frame's pixels to a small set of weighted buckets.
    /// </summary>
    /// <remarks>
    /// Every strategy runs on buckets rather than on raw pixels, which is what makes
    /// the expensive strategies affordable. A 128×72 sampled frame is 9,216 pixels
    /// but rarely more than a few hundred distinct 5-bit colours, so k-means over
    /// buckets does a fraction of the work of k-means over pixels and — because the
    /// buckets carry their populations as weights — converges to the same answer.
    /// <para>
    /// Five bits per channel is the deliberate choice. Four visibly banded skies in
    /// testing against synthetic gradients; six roughly quadruples the bucket count
    /// for a result no different once clusters are merged.
    /// </para>
    /// </remarks>
    internal static class ColorHistogram
    {
        private const int Bits = 5;
        private const int Shift = 8 - Bits;

        /// <summary>Builds the weighted buckets for a frame.</summary>
        /// <param name="rgb24">Pixels, three bytes each.</param>
        /// <param name="blackFloor">Pixels with every channel at or below this are skipped.</param>
        /// <returns>The buckets, in no meaningful order. Empty when everything was floored out.</returns>
        public static List<ColorBucket> Build(ReadOnlySpan<byte> rgb24, byte blackFloor)
        {
            // Keyed on the packed 15-bit index rather than a struct so the dictionary
            // hashes an int. At a few hundred entries per frame across hundreds of
            // thousands of frames per library run, that is not a micro-optimisation.
            var counts = new Dictionary<int, int>(512);

            for (var i = 0; i + 2 < rgb24.Length; i += 3)
            {
                byte r = rgb24[i], g = rgb24[i + 1], b = rgb24[i + 2];

                if (r <= blackFloor && g <= blackFloor && b <= blackFloor)
                {
                    continue;
                }

                var key = ((r >> Shift) << (Bits * 2)) | ((g >> Shift) << Bits) | (b >> Shift);
                counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
            }

            var buckets = new List<ColorBucket>(counts.Count);

            foreach (var (key, count) in counts)
            {
                // Reconstructed at the centre of the bucket, not its floor. Using the
                // floor drags every colour toward black by half a bucket, which over a
                // whole strip reads as a uniformly dimmer image.
                var half = (1 << Shift) / 2;
                var r = (byte)Math.Min(255, (((key >> (Bits * 2)) & 31) << Shift) + half);
                var g = (byte)Math.Min(255, (((key >> Bits) & 31) << Shift) + half);
                var b = (byte)Math.Min(255, ((key & 31) << Shift) + half);

                var rgb = new Rgb(r, g, b);
                buckets.Add(new ColorBucket(rgb.ToOklab(), rgb, count));
            }

            // Sorted so the output does not depend on dictionary enumeration order.
            // k-means++ seeding consumes the list in order, so without this the same
            // frame could yield a different stripe between runs — and a barcode that
            // changes when nothing changed is one nobody can trust or diff.
            buckets.Sort(static (x, y) => Key(x).CompareTo(Key(y)));

            return buckets;

            static int Key(ColorBucket bucket) =>
                (bucket.Rgb.R << 16) | (bucket.Rgb.G << 8) | bucket.Rgb.B;
        }

        /// <summary>
        /// The plain linear-light mean of a frame, used as the fallback when every
        /// pixel was floored out.
        /// </summary>
        /// <param name="rgb24">Pixels, three bytes each.</param>
        /// <returns>The mean colour, or black for an empty frame.</returns>
        public static Rgb LinearMean(ReadOnlySpan<byte> rgb24)
        {
            if (rgb24.Length < 3)
            {
                return Rgb.Black;
            }

            double r = 0, g = 0, b = 0;
            var n = 0;

            for (var i = 0; i + 2 < rgb24.Length; i += 3)
            {
                // Averaged in linear light, not in gamma-encoded sRGB. Averaging the
                // encoded values is the more common mistake and it comes out
                // noticeably dark; this at least makes the naive strategy naive for
                // only one reason rather than two.
                r += Linear(rgb24[i]);
                g += Linear(rgb24[i + 1]);
                b += Linear(rgb24[i + 2]);
                n++;
            }

            return new Rgb(Encode(r / n), Encode(g / n), Encode(b / n));
        }

        private static double Linear(byte channel)
        {
            var c = channel / 255d;
            return c <= 0.04045d ? c / 12.92d : Math.Pow((c + 0.055d) / 1.055d, 2.4d);
        }

        private static byte Encode(double linear)
        {
            var v = linear <= 0.0031308d
                ? linear * 12.92d
                : (1.055d * Math.Pow(linear, 1d / 2.4d)) - 0.055d;

            return (byte)Math.Clamp((int)Math.Round(v * 255d), 0, 255);
        }
    }
}
