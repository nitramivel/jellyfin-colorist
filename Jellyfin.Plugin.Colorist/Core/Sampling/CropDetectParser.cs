using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Colorist.Core.Sampling
{
    /// <summary>
    /// Reads the <c>crop=w:h:x:y</c> lines ffmpeg's cropdetect filter writes to
    /// stderr and decides which one to believe.
    /// </summary>
    public static partial class CropDetectParser
    {
        [GeneratedRegex(
            @"crop=(?<w>-?\d+):(?<h>-?\d+):(?<x>-?\d+):(?<y>-?\d+)",
            RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant)]
        private static partial Regex CropLine();

        /// <summary>Extracts every crop suggestion from a block of ffmpeg output.</summary>
        /// <param name="stderr">Whatever ffmpeg wrote.</param>
        /// <returns>The suggestions, in the order they appeared.</returns>
        public static IReadOnlyList<CropRect> ParseAll(string? stderr)
        {
            var results = new List<CropRect>();

            if (string.IsNullOrEmpty(stderr))
            {
                return results;
            }

            foreach (Match match in CropLine().Matches(stderr))
            {
                if (int.TryParse(match.Groups["w"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
                    && int.TryParse(match.Groups["h"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
                    && int.TryParse(match.Groups["x"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                    && int.TryParse(match.Groups["y"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
                {
                    results.Add(new CropRect(w, h, x, y));
                }
            }

            return results;
        }

        /// <summary>
        /// Picks the crop to actually use: the most frequently proposed plausible one.
        /// </summary>
        /// <param name="stderr">Whatever ffmpeg wrote.</param>
        /// <param name="sourceWidth">Source video width.</param>
        /// <param name="sourceHeight">Source video height.</param>
        /// <returns>The chosen crop, or null to sample the full frame.</returns>
        /// <remarks>
        /// <b>Modal, not last.</b> Taking the final line is the common approach and it
        /// is fragile: cropdetect emits a running best guess, so the last line
        /// reflects whatever the closing seconds looked like — and if sampling
        /// happens to end during a dark shot, that is the value you keep. The mode
        /// across the whole sampled range is the value the film spent most of its
        /// time agreeing on.
        /// <para>
        /// Implausible suggestions are discarded before counting rather than after,
        /// so a run of dark frames cannot win by weight of numbers.
        /// </para>
        /// </remarks>
        public static CropRect? SelectModal(string? stderr, int sourceWidth, int sourceHeight)
        {
            var counts = new Dictionary<CropRect, int>();

            foreach (var candidate in ParseAll(stderr))
            {
                if (!candidate.IsPlausibleFor(sourceWidth, sourceHeight))
                {
                    continue;
                }

                counts[candidate] = counts.TryGetValue(candidate, out var n) ? n + 1 : 1;
            }

            if (counts.Count == 0)
            {
                return null;
            }

            var best = default(CropRect);
            var bestCount = 0;

            foreach (var (rect, count) in counts)
            {
                // Ties broken toward the larger area so that two equally-supported
                // readings resolve to the one that throws less picture away.
                if (count > bestCount
                    || (count == bestCount && (long)rect.Width * rect.Height > (long)best.Width * best.Height))
                {
                    best = rect;
                    bestCount = count;
                }
            }

            // A crop that keeps the whole frame is not worth carrying through the
            // pipeline as a filter.
            if (best.Width == sourceWidth && best.Height == sourceHeight)
            {
                return null;
            }

            return best;
        }
    }
}
