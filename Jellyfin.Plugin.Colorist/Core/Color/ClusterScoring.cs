using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Colorist.Core.Color
{
    /// <summary>A group of buckets treated as one colour.</summary>
    /// <param name="Mean">The population-weighted centre, in Oklab.</param>
    /// <param name="Population">How many source pixels the cluster covers.</param>
    internal readonly record struct Cluster(Oklab Mean, long Population);

    /// <summary>
    /// Chooses which cluster represents the frame.
    /// </summary>
    /// <remarks>
    /// Kept apart from the clustering itself because they are genuinely two
    /// decisions, and conflating them is what makes "average versus dominant colour"
    /// such a confusing way to frame the problem. How you group pixels and which
    /// group you then hand back are independent; median cut and k-means disagree
    /// about the first and are interchangeable on the second.
    /// </remarks>
    internal static class ClusterScoring
    {
        /// <summary>
        /// Keeps a fully desaturated frame from scoring every cluster at zero.
        /// </summary>
        /// <remarks>
        /// Black-and-white footage and grey fog have no chroma anywhere, so without a
        /// floor the score collapses to zero for every candidate and the winner is
        /// whichever happened to be enumerated first. With it, population breaks the
        /// tie and the largest cluster wins — the right answer for a greyscale frame.
        /// Small enough that any real colour dominates it.
        /// </remarks>
        private const float ChromaFloor = 0.005f;

        /// <summary>Picks the winning cluster.</summary>
        /// <param name="clusters">Candidates; must not be empty.</param>
        /// <param name="dominanceExponent">See <see cref="ColorOptions.DominanceExponent"/>.</param>
        /// <returns>The representative colour in sRGB.</returns>
        public static Rgb Pick(IReadOnlyList<Cluster> clusters, float dominanceExponent)
        {
            var bestScore = float.NegativeInfinity;
            var best = clusters[0];

            foreach (var cluster in clusters)
            {
                if (cluster.Population == 0)
                {
                    continue;
                }

                var score = MathF.Pow(cluster.Population, dominanceExponent)
                    * (cluster.Mean.Chroma + ChromaFloor);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = cluster;
                }
            }

            return best.Mean.ToSrgb();
        }

        /// <summary>Collapses a run of buckets into one cluster.</summary>
        /// <param name="buckets">The buckets to merge.</param>
        /// <param name="start">First index, inclusive.</param>
        /// <param name="end">Last index, exclusive.</param>
        /// <returns>The merged cluster.</returns>
        public static Cluster Merge(IReadOnlyList<ColorBucket> buckets, int start, int end)
        {
            double l = 0, a = 0, b = 0;
            long population = 0;

            for (var i = start; i < end; i++)
            {
                var bucket = buckets[i];
                l += bucket.Lab.L * (double)bucket.Count;
                a += bucket.Lab.A * (double)bucket.Count;
                b += bucket.Lab.B * (double)bucket.Count;
                population += bucket.Count;
            }

            if (population == 0)
            {
                return new Cluster(default, 0);
            }

            return new Cluster(
                new Oklab((float)(l / population), (float)(a / population), (float)(b / population)),
                population);
        }
    }
}
