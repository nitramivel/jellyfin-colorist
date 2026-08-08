using System;

namespace Jellyfin.Plugin.Colorist.Core.Color
{
    /// <summary>
    /// Reduces one sampled frame to the single colour that will represent it as a
    /// stripe.
    /// </summary>
    /// <remarks>
    /// The frame arrives as a flat rgb24 buffer because that is what ffmpeg hands
    /// over, and it is deliberately treated as an unordered bag of pixels: no
    /// implementation here cares where in the frame a pixel sat. That is a real
    /// constraint, not an oversight — a centre-weighted or rule-of-thirds strategy
    /// would need the geometry, and adding one means widening this interface rather
    /// than quietly reinterpreting the buffer.
    /// </remarks>
    public interface IFrameColorStrategy
    {
        /// <summary>Gets the stable identifier stored in configuration.</summary>
        string Key { get; }

        /// <summary>Reduces a frame to one colour.</summary>
        /// <param name="rgb24">Pixels, three bytes each, length a multiple of three.</param>
        /// <param name="options">Clustering and scoring knobs.</param>
        /// <returns>The representative colour.</returns>
        Rgb Represent(ReadOnlySpan<byte> rgb24, ColorOptions options);
    }
}
