using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Colorist.Core.Color
{
    /// <summary>
    /// Weighted k-means in Oklab with k-means++ seeding, scored the same way as
    /// median cut.
    /// </summary>
    /// <remarks>
    /// <b>What this buys over median cut.</b> Median cut can only carve axis-aligned
    /// boxes, so a colour distribution lying diagonally across the space — a graded
    /// sunset running from orange through pink, say — gets sliced across its length
    /// into boxes that each hold part of one perceptual colour. k-means has no such
    /// constraint and will find the elongated cluster as one thing. That is a real
    /// difference on graded footage and close to no difference on ordinary interiors,
    /// which is why this is the opt-in and not the default.
    /// <para>
    /// <b>Determinism is not optional here.</b> k-means++ seeds from a random draw,
    /// and a barcode that comes out different on a re-run for a file that has not
    /// changed is one you cannot diff, cannot cache and cannot trust. The generator
    /// is seeded from a constant, and the buckets it consumes arrive in sorted order
    /// from <see cref="ColorHistogram"/>, so the same frame always gives the same
    /// stripe.
    /// </para>
    /// </remarks>
    public sealed class KMeansStrategy : IFrameColorStrategy
    {
        /// <summary>The configuration value selecting this strategy.</summary>
        public const string StrategyKey = "kmeans";

        /// <summary>
        /// Iteration ceiling. Weighted k-means over a few hundred buckets is
        /// essentially always settled well before this; the cap exists so a
        /// pathological frame cannot stall a library run, not as a tuning knob.
        /// </summary>
        private const int MaxIterations = 24;

        private const int Seed = 0x0C0107;

        /// <inheritdoc />
        public string Key => StrategyKey;

        /// <inheritdoc />
        public Rgb Represent(ReadOnlySpan<byte> rgb24, ColorOptions options)
        {
            var buckets = ColorHistogram.Build(rgb24, options.BlackFloor);

            if (buckets.Count == 0)
            {
                return ColorHistogram.LinearMean(rgb24);
            }

            var k = Math.Clamp(options.ClusterCount, 1, buckets.Count);
            var centroids = SeedCentroids(buckets, k);
            var assignment = new int[buckets.Count];

            for (var iteration = 0; iteration < MaxIterations; iteration++)
            {
                var moved = false;

                for (var i = 0; i < buckets.Count; i++)
                {
                    var nearest = Nearest(centroids, buckets[i].Lab);

                    if (nearest != assignment[i])
                    {
                        assignment[i] = nearest;
                        moved = true;
                    }
                }

                if (!moved && iteration > 0)
                {
                    break;
                }

                Recentre(buckets, assignment, centroids);
            }

            return ClusterScoring.Pick(Collect(buckets, assignment, k), options.DominanceExponent);
        }

        /// <summary>
        /// k-means++ seeding: first centroid at random, each subsequent one drawn
        /// with probability proportional to its squared distance from the nearest
        /// centroid already chosen.
        /// </summary>
        /// <remarks>
        /// Worth the extra pass. Seeding uniformly at random regularly puts two
        /// centroids inside the same large dull region and leaves the one vivid
        /// region — the part of the frame this whole plugin is trying to find —
        /// sharing a cluster with its surroundings.
        /// </remarks>
        private static Oklab[] SeedCentroids(List<ColorBucket> buckets, int k)
        {
            var random = new Random(Seed);
            var centroids = new Oklab[k];
            centroids[0] = buckets[random.Next(buckets.Count)].Lab;

            var distances = new double[buckets.Count];

            for (var c = 1; c < k; c++)
            {
                double total = 0;

                for (var i = 0; i < buckets.Count; i++)
                {
                    var nearest = double.MaxValue;

                    for (var j = 0; j < c; j++)
                    {
                        nearest = Math.Min(nearest, buckets[i].Lab.DistanceSquared(centroids[j]));
                    }

                    // Weighted by population as well as by distance, so a colour
                    // covering a quarter of the frame is a likelier seed than a
                    // equally-distant colour covering four pixels.
                    distances[i] = nearest * buckets[i].Count;
                    total += distances[i];
                }

                if (total <= 0)
                {
                    // Every remaining bucket coincides with a centroid — there is
                    // nothing left to separate, so the extra clusters stay empty and
                    // are dropped by the scorer.
                    for (var j = c; j < k; j++)
                    {
                        centroids[j] = centroids[c - 1];
                    }

                    break;
                }

                var threshold = random.NextDouble() * total;
                double running = 0;
                var picked = buckets.Count - 1;

                for (var i = 0; i < buckets.Count; i++)
                {
                    running += distances[i];

                    if (running >= threshold)
                    {
                        picked = i;
                        break;
                    }
                }

                centroids[c] = buckets[picked].Lab;
            }

            return centroids;
        }

        private static int Nearest(Oklab[] centroids, Oklab colour)
        {
            var best = 0;
            var bestDistance = float.MaxValue;

            for (var i = 0; i < centroids.Length; i++)
            {
                var distance = colour.DistanceSquared(centroids[i]);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return best;
        }

        private static void Recentre(List<ColorBucket> buckets, int[] assignment, Oklab[] centroids)
        {
            var l = new double[centroids.Length];
            var a = new double[centroids.Length];
            var b = new double[centroids.Length];
            var weight = new long[centroids.Length];

            for (var i = 0; i < buckets.Count; i++)
            {
                var c = assignment[i];
                var bucket = buckets[i];
                l[c] += bucket.Lab.L * (double)bucket.Count;
                a[c] += bucket.Lab.A * (double)bucket.Count;
                b[c] += bucket.Lab.B * (double)bucket.Count;
                weight[c] += bucket.Count;
            }

            for (var c = 0; c < centroids.Length; c++)
            {
                // An emptied cluster keeps its old centre rather than being reseeded.
                // It will simply attract nothing and be discarded by the scorer, and
                // reseeding it mid-run would reintroduce the non-determinism that the
                // fixed seed exists to remove.
                if (weight[c] == 0)
                {
                    continue;
                }

                centroids[c] = new Oklab(
                    (float)(l[c] / weight[c]),
                    (float)(a[c] / weight[c]),
                    (float)(b[c] / weight[c]));
            }
        }

        private static List<Cluster> Collect(List<ColorBucket> buckets, int[] assignment, int k)
        {
            var l = new double[k];
            var a = new double[k];
            var b = new double[k];
            var weight = new long[k];

            for (var i = 0; i < buckets.Count; i++)
            {
                var c = assignment[i];
                var bucket = buckets[i];
                l[c] += bucket.Lab.L * (double)bucket.Count;
                a[c] += bucket.Lab.A * (double)bucket.Count;
                b[c] += bucket.Lab.B * (double)bucket.Count;
                weight[c] += bucket.Count;
            }

            var clusters = new List<Cluster>(k);

            for (var c = 0; c < k; c++)
            {
                if (weight[c] == 0)
                {
                    continue;
                }

                clusters.Add(new Cluster(
                    new Oklab(
                        (float)(l[c] / weight[c]),
                        (float)(a[c] / weight[c]),
                        (float)(b[c] / weight[c])),
                    weight[c]));
            }

            return clusters;
        }
    }
}
