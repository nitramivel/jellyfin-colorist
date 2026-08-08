using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Colorist.Core.Color
{
    /// <summary>
    /// Heckbert's median cut, run in Oklab, with the winning box chosen by
    /// population and chroma together.
    /// </summary>
    /// <remarks>
    /// <b>Why this is the default.</b> It is deterministic, it terminates in a fixed
    /// number of splits with no convergence to wait on, and its cost is dominated by
    /// sorting a few hundred buckets. Over a full library that is hundreds of
    /// thousands of frames, and k-means' extra factor of thirty is the difference
    /// between an overnight run and a weekend one — for a result that, on the kind
    /// of colour distribution a film frame actually has, is usually the same stripe.
    /// <para>
    /// <b>Where it is run matters as much as which algorithm it is.</b> Classic
    /// median cut splits axis-aligned boxes in RGB, so its boxes carve the colour
    /// solid along axes that mean nothing perceptually. Splitting in Oklab keeps the
    /// same cheap algorithm but makes "the longest axis of this box" a statement
    /// about how the colours in it actually look.
    /// </para>
    /// </remarks>
    public sealed class MedianCutStrategy : IFrameColorStrategy
    {
        /// <summary>The configuration value selecting this strategy.</summary>
        public const string StrategyKey = "mediancut";

        /// <inheritdoc />
        public string Key => StrategyKey;

        /// <inheritdoc />
        public Rgb Represent(ReadOnlySpan<byte> rgb24, ColorOptions options)
        {
            var buckets = ColorHistogram.Build(rgb24, options.BlackFloor);

            // Everything was below the black floor: a genuinely black frame, a hard
            // cut, or the inside of a fade. Falling through to the mean returns black
            // rather than an arbitrary colour, which is the honest answer.
            if (buckets.Count == 0)
            {
                return ColorHistogram.LinearMean(rgb24);
            }

            var boxes = Split(buckets, Math.Max(1, options.ClusterCount));
            var clusters = new List<Cluster>(boxes.Count);

            foreach (var (start, end) in boxes)
            {
                clusters.Add(ClusterScoring.Merge(buckets, start, end));
            }

            return ClusterScoring.Pick(clusters, options.DominanceExponent);
        }

        /// <summary>
        /// Recursively halves the bucket list into ranges, sorting in place.
        /// </summary>
        /// <remarks>
        /// The boxes are contiguous ranges over one list that gets reordered as it
        /// goes, rather than separate collections, so splitting never allocates and
        /// merging is a walk over a slice.
        /// </remarks>
        private static List<(int Start, int End)> Split(List<ColorBucket> buckets, int target)
        {
            var boxes = new List<(int Start, int End)> { (0, buckets.Count) };

            while (boxes.Count < target)
            {
                var chosen = -1;
                var bestPriority = 0d;
                var chosenAxis = 0;

                for (var i = 0; i < boxes.Count; i++)
                {
                    var (start, end) = boxes[i];

                    if (end - start < 2)
                    {
                        continue;
                    }

                    var (axis, extent) = LongestAxis(buckets, start, end);

                    if (extent <= 0)
                    {
                        continue;
                    }

                    long population = 0;
                    for (var j = start; j < end; j++)
                    {
                        population += buckets[j].Count;
                    }

                    // Population alone (Heckbert's rule) keeps splitting a large flat
                    // sky that has nothing left to separate; extent alone chases a
                    // handful of outlying pixels spread across the gamut. The product
                    // asks the question that matters — which box holds both a lot of
                    // pixels and real disagreement about their colour.
                    var priority = population * extent;

                    if (priority > bestPriority)
                    {
                        bestPriority = priority;
                        chosen = i;
                        chosenAxis = axis;
                    }
                }

                if (chosen < 0)
                {
                    break;
                }

                var (s, e) = boxes[chosen];
                var mid = SplitAt(buckets, s, e, chosenAxis);

                if (mid <= s || mid >= e)
                {
                    break;
                }

                boxes[chosen] = (s, mid);
                boxes.Add((mid, e));
            }

            return boxes;
        }

        private static (int Axis, double Extent) LongestAxis(List<ColorBucket> buckets, int start, int end)
        {
            float lMin = float.MaxValue, aMin = float.MaxValue, bMin = float.MaxValue;
            float lMax = float.MinValue, aMax = float.MinValue, bMax = float.MinValue;

            for (var i = start; i < end; i++)
            {
                var lab = buckets[i].Lab;
                lMin = MathF.Min(lMin, lab.L);
                lMax = MathF.Max(lMax, lab.L);
                aMin = MathF.Min(aMin, lab.A);
                aMax = MathF.Max(aMax, lab.A);
                bMin = MathF.Min(bMin, lab.B);
                bMax = MathF.Max(bMax, lab.B);
            }

            var dl = lMax - lMin;
            var da = aMax - aMin;
            var db = bMax - bMin;

            if (dl >= da && dl >= db)
            {
                return (0, dl);
            }

            return da >= db ? (1, da) : (2, db);
        }

        private static int SplitAt(List<ColorBucket> buckets, int start, int end, int axis)
        {
            Comparison<ColorBucket> byAxis = axis switch
            {
                0 => static (x, y) => x.Lab.L.CompareTo(y.Lab.L),
                1 => static (x, y) => x.Lab.A.CompareTo(y.Lab.A),
                _ => static (x, y) => x.Lab.B.CompareTo(y.Lab.B),
            };

            buckets.Sort(start, end - start, Comparer<ColorBucket>.Create(byAxis));

            long total = 0;
            for (var i = start; i < end; i++)
            {
                total += buckets[i].Count;
            }

            // The median by pixel population, not by bucket index. Splitting at the
            // middle bucket would divide the colours present rather than the pixels
            // present, so a thousand-pixel colour and a one-pixel colour would carry
            // equal weight in deciding where the boundary falls.
            long running = 0;
            for (var i = start; i < end - 1; i++)
            {
                running += buckets[i].Count;

                if (running * 2 >= total)
                {
                    return i + 1;
                }
            }

            return end - 1;
        }
    }
}
