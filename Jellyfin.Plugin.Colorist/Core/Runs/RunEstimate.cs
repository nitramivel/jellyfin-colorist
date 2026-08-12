using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Colorist.Core.Runs
{
    /// <summary>
    /// Works out how long is left, from nothing but when items finished.
    /// </summary>
    /// <remarks>
    /// <b>Throughput, not per-item duration.</b> A run processes several items at
    /// once, so "average item took 40 seconds times 900 items left" overstates the
    /// answer by the concurrency factor. Counting completions per unit of wall-clock
    /// time measures the thing actually being predicted and needs to know nothing
    /// about how many workers there are.
    /// <para>
    /// <b>Windowed, because a library is not uniform.</b> Colorist's items range from
    /// a 22-minute episode to a three-hour film, and Jellyfin hands them over grouped
    /// by library — so a run's rate genuinely changes partway through as it crosses
    /// from films into episodes. An average over the whole run would spend the second
    /// half slowly correcting an estimate formed during the first. The window follows
    /// the recent rate instead, and is wide enough that one slow file does not swing
    /// it.
    /// </para>
    /// <para>
    /// Pure and separated from the run log because it is the one part of this feature
    /// with arithmetic worth testing, and there is no server here to test it against.
    /// </para>
    /// </remarks>
    public static class RunEstimate
    {
        /// <summary>
        /// How many recent completions the rate is measured over.
        /// </summary>
        /// <remarks>
        /// Twenty is roughly a minute of a default run. Fewer and one unusually long
        /// film visibly jerks the estimate; many more and it stops being recent.
        /// </remarks>
        public const int Window = 20;

        /// <summary>
        /// Completions needed before an estimate is offered at all.
        /// </summary>
        /// <remarks>
        /// Three. The first completion says nothing about rate, and the second is
        /// mostly startup cost — ffprobe, the first decode, the OS filling its
        /// caches. Showing a wildly wrong number early is worse than showing none:
        /// people read the first estimate and remember it.
        /// </remarks>
        public const int Minimum = 3;

        /// <summary>
        /// Estimates the time left.
        /// </summary>
        /// <param name="completions">
        /// When each finished item finished, oldest first. Only the tail is read.
        /// </param>
        /// <param name="total">How many items the run has in all.</param>
        /// <param name="now">The current time.</param>
        /// <returns>The estimate, or null when there is not yet enough to say.</returns>
        public static TimeSpan? Remaining(IReadOnlyList<DateTime> completions, int total, DateTime now)
        {
            ArgumentNullException.ThrowIfNull(completions);

            var rate = ItemsPerSecond(completions, now);

            if (rate is null or <= 0)
            {
                return null;
            }

            var remaining = total - completions.Count;

            if (remaining <= 0)
            {
                return TimeSpan.Zero;
            }

            var seconds = remaining / rate.Value;

            // A fortnight is not an estimate, it is a number that happens to be
            // large. Anything past this is reported as unknown, which at least says
            // so honestly.
            return seconds > TimeSpan.FromDays(14).TotalSeconds
                ? null
                : TimeSpan.FromSeconds(seconds);
        }

        /// <summary>
        /// The recent completion rate, in items per second.
        /// </summary>
        /// <param name="completions">When each finished item finished, oldest first.</param>
        /// <param name="now">The current time.</param>
        /// <returns>The rate, or null when there is not enough to measure one.</returns>
        /// <remarks>
        /// Measured to <paramref name="now"/> rather than to the last completion.
        /// Those differ exactly when the run has stalled on something slow, and it is
        /// the stall that most needs to show up — an estimate that keeps counting
        /// down while nothing is finishing is the one people stop trusting.
        /// </remarks>
        public static double? ItemsPerSecond(IReadOnlyList<DateTime> completions, DateTime now)
        {
            ArgumentNullException.ThrowIfNull(completions);

            if (completions.Count < Minimum)
            {
                return null;
            }

            var take = Math.Min(Window, completions.Count);
            var first = completions[completions.Count - take];

            // The window's own completions, less the one that only marks its start.
            var counted = take - 1;
            var elapsed = (now - first).TotalSeconds;

            if (counted <= 0 || elapsed <= 0)
            {
                return null;
            }

            return counted / elapsed;
        }

        /// <summary>
        /// Renders an estimate the way the settings page shows it.
        /// </summary>
        /// <param name="remaining">The estimate, or null.</param>
        /// <returns>A short human phrase.</returns>
        /// <remarks>
        /// Deliberately coarse, and coarser the further out it looks. A run with two
        /// hours to go does not know that to the second, and printing "1h 58m 12s"
        /// claims a precision the arithmetic does not have.
        /// </remarks>
        public static string Describe(TimeSpan? remaining)
        {
            if (remaining is null)
            {
                return "estimating…";
            }

            var seconds = remaining.Value.TotalSeconds;

            if (seconds < 10)
            {
                return "almost done";
            }

            if (seconds < 90)
            {
                return $"about {Math.Round(seconds / 10) * 10:0} seconds left";
            }

            if (seconds < 5400)
            {
                return $"about {Math.Round(seconds / 60):0} minutes left";
            }

            var hours = seconds / 3600;

            return hours < 10
                ? $"about {hours:0.#} hours left"
                : $"about {Math.Round(hours):0} hours left";
        }
    }
}
