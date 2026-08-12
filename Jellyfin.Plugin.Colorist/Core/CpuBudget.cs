using System;

namespace Jellyfin.Plugin.Colorist.Core
{
    /// <summary>
    /// Turns "use this much of the machine" into a number of workers.
    /// </summary>
    /// <remarks>
    /// <b>Workers are the only CPU dial there is.</b> Each item is sampled by one
    /// ffmpeg, and each ffmpeg is capped at one decoder thread by default, so the
    /// number of items in flight is very close to the number of cores in use. That
    /// makes a percentage of the processor count a fair statement of intent rather
    /// than a fiction.
    /// <para>
    /// <b>It is a budget, not a ceiling.</b> Nothing here stops ffmpeg exceeding its
    /// share for a moment, and raising the per-process thread cap multiplies the real
    /// usage. What holds the line under contention is below-normal process priority,
    /// which is already applied: this setting governs how much of an <i>idle</i>
    /// machine a run helps itself to, and the priority governs what happens when
    /// somebody starts watching something. The two are complementary, which is why
    /// this is deliberately not implemented as processor affinity — pinning workers
    /// to fixed cores would stop the scheduler moving them out of a transcode's way,
    /// making playback worse in exactly the case the priority exists to protect.
    /// </para>
    /// <para>
    /// <b>The processor count is the one the server can see.</b> .NET reports the
    /// cgroup quota inside a container, so on a Jellyfin limited to four CPUs this is
    /// a percentage of four rather than of the host — which is the number the owner
    /// meant.
    /// </para>
    /// </remarks>
    public static class CpuBudget
    {
        /// <summary>The least of the machine a run may be asked to take.</summary>
        /// <remarks>
        /// Five percent, not zero. A budget that resolves to no workers is a run that
        /// never finishes, so the floor is one worker regardless; this only stops the
        /// settings page offering a number that reads as "pause".
        /// </remarks>
        public const int MinimumPercent = 5;

        /// <summary>The default share of the machine.</summary>
        /// <remarks>
        /// A quarter, which is what the hardcoded rule this replaced worked out to.
        /// The job being competed with is a transcode for somebody actually watching
        /// something, and leaving three quarters alone is a reasonable default for
        /// work nobody is waiting on.
        /// </remarks>
        public const int DefaultPercent = 25;

        /// <summary>The most items that will ever run at once.</summary>
        /// <remarks>
        /// Thirty-two. Past this the run is bounded by disk and by however many
        /// files the storage will stream at once, not by CPU, and each worker still
        /// costs a process and a set of pipes.
        /// </remarks>
        public const int MaximumWorkers = 32;

        /// <summary>How many items to process at once.</summary>
        /// <param name="explicitWorkers">An explicit item count; zero means use the budget.</param>
        /// <param name="percent">The share of the machine to use.</param>
        /// <param name="processorCount">Processors the server can see.</param>
        /// <returns>The worker count, at least one.</returns>
        /// <remarks>
        /// An explicit count wins outright. Somebody who has typed a number has
        /// measured their own machine, and silently overriding that with a percentage
        /// would make the field they filled in do nothing.
        /// </remarks>
        public static int Workers(int explicitWorkers, int percent, int processorCount)
        {
            if (explicitWorkers > 0)
            {
                return Math.Min(explicitWorkers, MaximumWorkers);
            }

            var cores = Math.Max(1, processorCount);
            var share = Math.Clamp(percent, MinimumPercent, 100);

            // Rounded rather than truncated. Integer division gave 100% of a
            // three-core machine as three, but 50% as one — the half that rounds down
            // is the half that matters on the small servers this most affects.
            var workers = (int)Math.Round(cores * share / 100d, MidpointRounding.AwayFromZero);

            return Math.Clamp(workers, 1, Math.Min(cores, MaximumWorkers));
        }

        /// <summary>Explains the resolved figure the way the settings page shows it.</summary>
        /// <param name="explicitWorkers">An explicit item count; zero means use the budget.</param>
        /// <param name="percent">The share of the machine to use.</param>
        /// <param name="processorCount">Processors the server can see.</param>
        /// <returns>A one-line description.</returns>
        public static string Describe(int explicitWorkers, int percent, int processorCount)
        {
            var workers = Workers(explicitWorkers, percent, processorCount);
            var plural = workers == 1 ? "" : "s";

            return explicitWorkers > 0
                ? $"{workers} item{plural} at a time, as set"
                : $"{Math.Clamp(percent, MinimumPercent, 100)}% of {Math.Max(1, processorCount)} processors"
                    + $" — {workers} item{plural} at a time";
        }
    }
}
