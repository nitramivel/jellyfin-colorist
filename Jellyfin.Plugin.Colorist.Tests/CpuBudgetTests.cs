using Jellyfin.Plugin.Colorist.Core;
using Xunit;

namespace Jellyfin.Plugin.Colorist.Tests
{
    /// <summary>
    /// Turning a share of the machine into a worker count.
    /// </summary>
    /// <remarks>
    /// Pure arithmetic with a processor count passed in, which is the whole reason it
    /// was lifted out of <c>BarcodeService</c>: the rule can be checked here against
    /// machine sizes this one does not have.
    /// </remarks>
    public class CpuBudgetTests
    {
        [Theory]
        [InlineData(20, 25, 5)]     // the reported case: 25% of 20 cores
        [InlineData(12, 25, 3)]     // this development machine
        [InlineData(20, 50, 10)]
        [InlineData(20, 100, 20)]
        [InlineData(8, 25, 2)]
        [InlineData(64, 25, 16)]
        public void SpendsTheShareOfTheProcessors(int cores, int percent, int expected)
        {
            Assert.Equal(expected, CpuBudget.Workers(0, percent, cores));
        }

        [Theory]
        [InlineData(4, 25, 1)]      // exactly one
        [InlineData(2, 25, 1)]      // half a core rounds to one, not zero
        [InlineData(1, 100, 1)]
        [InlineData(1, 5, 1)]
        public void NeverResolvesToNoWorkersAtAll(int cores, int percent, int expected)
        {
            // A budget of zero workers is a run that never finishes.
            Assert.Equal(expected, CpuBudget.Workers(0, percent, cores));
        }

        [Fact]
        public void RoundsRatherThanTruncating()
        {
            // Integer division made 50% of three cores one worker, and 100% three.
            // The half that rounds down is the half that matters on small servers.
            Assert.Equal(2, CpuBudget.Workers(0, 50, 3));
            Assert.Equal(3, CpuBudget.Workers(0, 100, 3));
            Assert.Equal(3, CpuBudget.Workers(0, 50, 6));
        }

        [Fact]
        public void NeverAsksForMoreWorkersThanThereAreProcessors()
        {
            // 100% of four cores is four items, not some larger number that would
            // only add context switching.
            Assert.Equal(4, CpuBudget.Workers(0, 100, 4));
        }

        [Fact]
        public void CapsTheWorkerCountWhateverTheMachine()
        {
            // Past this the run is bounded by disk rather than by CPU.
            Assert.Equal(CpuBudget.MaximumWorkers, CpuBudget.Workers(0, 100, 256));
        }

        [Theory]
        [InlineData(5)]
        [InlineData(1)]
        [InlineData(32)]
        public void AnExplicitCountWinsOutright(int configured)
        {
            // Somebody who typed a number has measured their own machine; a
            // percentage must not quietly override them.
            Assert.Equal(configured, CpuBudget.Workers(configured, 100, 64));
            Assert.Equal(configured, CpuBudget.Workers(configured, 5, 64));
        }

        [Fact]
        public void AnExplicitCountIsStillCapped()
        {
            Assert.Equal(CpuBudget.MaximumWorkers, CpuBudget.Workers(9999, 25, 64));
        }

        [Fact]
        public void AnExplicitCountMayExceedTheProcessorCount()
        {
            // Deliberate: sampling is not purely CPU bound, and somebody on slow
            // network storage may genuinely want more items in flight than cores.
            Assert.Equal(8, CpuBudget.Workers(8, 25, 2));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-40)]
        [InlineData(500)]
        public void ClampsANonsensePercentage(int percent)
        {
            // The XML on disk can hold anything a hand edit or a failed upgrade left.
            var workers = CpuBudget.Workers(0, percent, 16);

            Assert.InRange(workers, 1, 16);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void SurvivesAMachineThatClaimsNoProcessors(int cores)
        {
            Assert.Equal(1, CpuBudget.Workers(0, 50, cores));
        }

        [Fact]
        public void DescribesTheShareTheWayThePageShowsIt()
        {
            Assert.Equal(
                "25% of 20 processors — 5 items at a time",
                CpuBudget.Describe(0, 25, 20));
        }

        [Fact]
        public void DescribesASingleWorkerWithoutTheStraySplural()
        {
            Assert.Equal("5% of 4 processors — 1 item at a time", CpuBudget.Describe(0, 5, 4));
        }

        [Fact]
        public void DescribesAnOverrideAsAnOverride()
        {
            Assert.Equal("6 items at a time, as set", CpuBudget.Describe(6, 25, 20));
        }

        [Fact]
        public void TheDefaultMatchesTheRuleItReplaced()
        {
            // The old hardcoded behaviour was a quarter of the processors, and an
            // upgrade must not silently change how hard the server gets worked.
            for (var cores = 1; cores <= 64; cores++)
            {
                var old = System.Math.Max(1, cores / 4);
                var now = CpuBudget.Workers(0, CpuBudget.DefaultPercent, cores);

                Assert.True(
                    System.Math.Abs(now - old) <= 1,
                    $"{cores} cores: was {old}, now {now}");
            }
        }
    }
}
