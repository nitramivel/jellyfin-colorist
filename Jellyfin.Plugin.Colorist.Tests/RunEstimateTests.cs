using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Colorist.Core.Runs;
using Xunit;

namespace Jellyfin.Plugin.Colorist.Tests
{
    /// <summary>
    /// The estimate shown on the settings page.
    /// </summary>
    /// <remarks>
    /// The one part of the run log with arithmetic worth getting wrong, and the only
    /// part testable without a server: everything else is file I/O and a lock.
    /// </remarks>
    public class RunEstimateTests
    {
        private static readonly DateTime Start = new(2026, 8, 12, 3, 0, 0, DateTimeKind.Utc);

        /// <summary>Completions at a steady interval, oldest first.</summary>
        private static List<DateTime> Steady(int count, double secondsApart) =>
            Enumerable.Range(0, count)
                .Select(i => Start.AddSeconds(i * secondsApart))
                .ToList();

        [Fact]
        public void SaysNothingBeforeThereIsEnoughToSayItFrom()
        {
            // Two completions is one interval, and that interval is mostly startup:
            // ffprobe, the first decode, the OS filling its caches.
            for (var count = 0; count < RunEstimate.Minimum; count++)
            {
                var completions = Steady(count, 10);

                Assert.Null(RunEstimate.Remaining(completions, 100, Start.AddSeconds(count * 10)));
                Assert.Null(RunEstimate.ItemsPerSecond(completions, Start.AddSeconds(count * 10)));
            }
        }

        [Fact]
        public void MeasuresTheRateFromWallClockNotFromItemDuration()
        {
            // Ten completions, ten seconds apart. Nine intervals over ninety
            // seconds is 0.1 items per second regardless of how many workers
            // produced them — which is the whole reason throughput is used rather
            // than an average item duration.
            var completions = Steady(10, 10);
            var now = Start.AddSeconds(90);

            var rate = RunEstimate.ItemsPerSecond(completions, now);

            Assert.NotNull(rate);
            Assert.Equal(0.1, rate!.Value, 3);
        }

        [Fact]
        public void ProjectsTheRemainingItemsAtThatRate()
        {
            // 10 of 100 done at 0.1/s. Ninety left, nine hundred seconds.
            var completions = Steady(10, 10);
            var remaining = RunEstimate.Remaining(completions, 100, Start.AddSeconds(90));

            Assert.NotNull(remaining);
            Assert.Equal(900, remaining!.Value.TotalSeconds, 1);
        }

        [Fact]
        public void OnlyTheRecentWindowCounts()
        {
            // A run that crawled through films and is now flying through episodes.
            // An average over the whole run would still be reporting the crawl.
            var completions = new List<DateTime>();
            var at = Start;

            for (var i = 0; i < 40; i++)
            {
                at = at.AddSeconds(60);
                completions.Add(at);
            }

            for (var i = 0; i < 20; i++)
            {
                at = at.AddSeconds(2);
                completions.Add(at);
            }

            var rate = RunEstimate.ItemsPerSecond(completions, at);

            // The recent stretch is one item every two seconds. An all-time average
            // would be near 0.02, which is twenty-five times slower.
            Assert.NotNull(rate);
            Assert.True(rate!.Value > 0.4, $"expected the recent rate, got {rate}");
        }

        [Fact]
        public void AStalledRunsEstimateGrows()
        {
            // Measured to now rather than to the last completion, so a run wedged on
            // one enormous file stops claiming it is nearly finished.
            var completions = Steady(10, 10);

            var promptly = RunEstimate.Remaining(completions, 100, Start.AddSeconds(90));
            var stalled = RunEstimate.Remaining(completions, 100, Start.AddSeconds(90 + 600));

            Assert.NotNull(promptly);
            Assert.NotNull(stalled);
            Assert.True(
                stalled!.Value > promptly!.Value,
                "an estimate that keeps counting down while nothing finishes is the one people stop trusting");
        }

        [Fact]
        public void ReturnsZeroWhenEverythingIsDone()
        {
            var completions = Steady(50, 5);

            Assert.Equal(TimeSpan.Zero, RunEstimate.Remaining(completions, 50, Start.AddSeconds(250)));
        }

        [Fact]
        public void RefusesToGuessBeyondAFortnight()
        {
            // One item every ten minutes with a million to go is not an estimate,
            // it is a number that happens to be large.
            var completions = Steady(5, 600);

            Assert.Null(RunEstimate.Remaining(completions, 1_000_000, Start.AddSeconds(2400)));
        }

        [Fact]
        public void SurvivesCompletionsThatAllLandOnTheSameInstant()
        {
            // A fast delete run can finish several items inside one clock tick, and
            // dividing by that zero would produce an infinity on the settings page.
            var completions = Enumerable.Repeat(Start, 10).ToList();

            Assert.Null(RunEstimate.ItemsPerSecond(completions, Start));
            Assert.Null(RunEstimate.Remaining(completions, 100, Start));
        }

        [Fact]
        public void RejectsANullList()
        {
            Assert.Throws<ArgumentNullException>(() => RunEstimate.Remaining(null!, 10, Start));
            Assert.Throws<ArgumentNullException>(() => RunEstimate.ItemsPerSecond(null!, Start));
        }

        [Theory]
        [InlineData(null, "estimating…")]
        [InlineData(4, "almost done")]
        [InlineData(42, "about 40 seconds left")]
        [InlineData(600, "about 10 minutes left")]
        [InlineData(9000, "about 2.5 hours left")]
        [InlineData(180000, "about 50 hours left")]
        public void WordsTheEstimateCoarselyTheFurtherOutItLooks(int? seconds, string expected)
        {
            var remaining = seconds is null ? (TimeSpan?)null : TimeSpan.FromSeconds(seconds.Value);

            Assert.Equal(expected, RunEstimate.Describe(remaining));
        }
    }
}
