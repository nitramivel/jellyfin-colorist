using System;

namespace Jellyfin.Plugin.Colorist.Core.Color
{
    /// <summary>
    /// The straight average of every pixel.
    /// </summary>
    /// <remarks>
    /// Kept as a shipped option, not as dead code, because it is the baseline the
    /// other two are arguing against and the only way to see the difference on your
    /// own library is to be able to switch to it.
    /// <para>
    /// It is also the one that goes brown, and it is worth being precise about why,
    /// because the reason is not "averaging is imprecise". Averaging is exact; it is
    /// answering a different question. A red coat against green foliage really does
    /// have a mean around mud — opposing hues cancel through the neutral axis, and
    /// the mean of two vivid colours is less colourful than either. No amount of
    /// care in the arithmetic fixes that, which is why the alternatives cluster
    /// first and never average across a cluster boundary.
    /// </para>
    /// </remarks>
    public sealed class MeanStrategy : IFrameColorStrategy
    {
        /// <summary>The configuration value selecting this strategy.</summary>
        public const string StrategyKey = "mean";

        /// <inheritdoc />
        public string Key => StrategyKey;

        /// <inheritdoc />
        public Rgb Represent(ReadOnlySpan<byte> rgb24, ColorOptions options) =>
            ColorHistogram.LinearMean(rgb24);
    }
}
