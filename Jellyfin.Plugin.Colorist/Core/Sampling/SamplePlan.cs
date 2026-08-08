using System;

namespace Jellyfin.Plugin.Colorist.Core.Sampling
{
    /// <summary>
    /// What to sample from one item: the slice of runtime to look at, and how many
    /// stripes to cut it into.
    /// </summary>
    /// <param name="StartSeconds">Where sampling begins.</param>
    /// <param name="EndSeconds">Where sampling ends.</param>
    /// <param name="Columns">How many stripes the strip will have.</param>
    public readonly record struct SamplePlan(double StartSeconds, double EndSeconds, int Columns)
    {
        /// <summary>Gets the length of the sampled window.</summary>
        public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);

        /// <summary>Gets the interval between stripe centres.</summary>
        public double IntervalSeconds => Columns > 0 ? DurationSeconds / Columns : 0;
    }

    /// <summary>Works out the sample plan for an item.</summary>
    /// <remarks>
    /// <b>Fixed column count, not fixed interval.</b> The x-axis of a barcode means
    /// "how far through the film", so it should be a fraction of runtime, not a
    /// clock. A fixed five-second interval gives a 22-minute episode 264 stripes and
    /// a three-hour film 2,160 — images of wildly different widths that cannot be
    /// placed side by side, and a per-item cost that scales with runtime for no
    /// benefit. A fixed count makes every barcode the same shape and comparable, and
    /// makes the cost of an item roughly constant regardless of length.
    /// <para>
    /// <b>The head and tail trims exist for credits.</b> A long single-colour credit
    /// roll is real footage and the sampler will faithfully report it as forty
    /// stripes of black, which swamps the end of the image. The trim is a blunt
    /// percentage rather than black-run detection on purpose: detection misfires on
    /// deliberately dark cinematography, and a plugin that quietly eats the last
    /// eight minutes of a film graded like <i>Se7en</i> is worse than one that
    /// includes some credits. Detection is available as an opt-in in configuration.
    /// </para>
    /// </remarks>
    public static class SamplePlanner
    {
        /// <summary>Below this there is nothing worth sampling.</summary>
        public const double MinimumRuntimeSeconds = 5;

        /// <summary>Builds the plan.</summary>
        /// <param name="runtimeSeconds">Item runtime.</param>
        /// <param name="requestedColumns">Configured stripe count.</param>
        /// <param name="headTrimPercent">Fraction of runtime to skip at the start, 0-40.</param>
        /// <param name="tailTrimPercent">Fraction of runtime to skip at the end, 0-40.</param>
        /// <returns>The plan, or null when the item is too short to sample.</returns>
        public static SamplePlan? Plan(
            double runtimeSeconds,
            int requestedColumns,
            double headTrimPercent,
            double tailTrimPercent)
        {
            if (double.IsNaN(runtimeSeconds) || runtimeSeconds < MinimumRuntimeSeconds)
            {
                return null;
            }

            // Clamped so a mistyped 90/90 in the settings file cannot ask for a
            // negative window. The two are clamped together rather than separately
            // because 40 and 40 is fine while 40 and 70 is not.
            var head = Math.Clamp(headTrimPercent, 0, 40);
            var tail = Math.Clamp(tailTrimPercent, 0, 40);

            if (head + tail > 80)
            {
                var scale = 80 / (head + tail);
                head *= scale;
                tail *= scale;
            }

            var start = runtimeSeconds * (head / 100d);
            var end = runtimeSeconds * (1 - (tail / 100d));

            if (end - start < MinimumRuntimeSeconds)
            {
                start = 0;
                end = runtimeSeconds;
            }

            var columns = Math.Clamp(requestedColumns, 16, 4000);

            // A short item gets fewer stripes rather than the full count. Asking for
            // 1,000 samples from a 90-second music video means a stripe every 0.09
            // seconds, which is far below the rate at which anything on screen
            // changes: it costs the same decode and produces a thousand columns of
            // near-duplicate colour. One stripe per quarter-second is already finer
            // than a cut.
            var affordable = (int)((end - start) * 4);

            return new SamplePlan(start, end, Math.Max(16, Math.Min(columns, affordable)));
        }
    }
}
