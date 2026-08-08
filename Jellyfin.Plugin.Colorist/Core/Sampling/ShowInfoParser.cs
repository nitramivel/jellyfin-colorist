using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Colorist.Core.Sampling
{
    /// <summary>
    /// Reads presentation timestamps out of ffmpeg's <c>showinfo</c> output.
    /// </summary>
    public static partial class ShowInfoParser
    {
        [GeneratedRegex(
            @"pts_time:(?<t>-?\d+(?:\.\d+)?)",
            RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant)]
        private static partial Regex PtsTime();

        /// <summary>Extracts every timestamp, in the order the frames were emitted.</summary>
        /// <param name="stderr">Whatever ffmpeg wrote to stderr.</param>
        /// <returns>The timestamps in seconds.</returns>
        /// <remarks>
        /// These are timestamps within the decoded stream, so they already account for
        /// the input seek: with <c>-ss</c> before <c>-i</c> ffmpeg rebases the output,
        /// and the first frame reports a time near zero rather than near the seek
        /// point. Callers treat them as offsets from the start of the sampled window,
        /// not as absolute positions in the file.
        /// </remarks>
        public static IReadOnlyList<double> ParseTimestamps(string? stderr)
        {
            var times = new List<double>();

            if (string.IsNullOrEmpty(stderr))
            {
                return times;
            }

            foreach (Match match in PtsTime().Matches(stderr))
            {
                if (double.TryParse(
                        match.Groups["t"].Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var seconds))
                {
                    times.Add(seconds);
                }
            }

            return times;
        }
    }
}
