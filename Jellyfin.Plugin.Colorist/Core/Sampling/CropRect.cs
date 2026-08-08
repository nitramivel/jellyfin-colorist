using System;
using System.Globalization;

namespace Jellyfin.Plugin.Colorist.Core.Sampling
{
    /// <summary>A crop window, in the coordinates of the source video.</summary>
    /// <param name="Width">Width of the kept region.</param>
    /// <param name="Height">Height of the kept region.</param>
    /// <param name="X">Left edge of the kept region.</param>
    /// <param name="Y">Top edge of the kept region.</param>
    public readonly record struct CropRect(int Width, int Height, int X, int Y)
    {
        /// <summary>Renders as the argument to ffmpeg's <c>crop</c> filter.</summary>
        /// <returns>A string of the form <c>crop=w:h:x:y</c>.</returns>
        public string ToFilter() => string.Create(
            CultureInfo.InvariantCulture,
            $"crop={Width}:{Height}:{X}:{Y}");

        /// <summary>
        /// Whether this crop is sane enough to apply to a source of the given size.
        /// </summary>
        /// <param name="sourceWidth">Source video width.</param>
        /// <param name="sourceHeight">Source video height.</param>
        /// <returns>Whether the crop should be trusted.</returns>
        /// <remarks>
        /// <b>The guard matters more than the detection.</b> cropdetect is reading
        /// pictures, and a stretch of genuinely dark footage looks exactly like a
        /// letterbox bar to it. Left unchecked it will happily propose keeping a
        /// quarter of the frame, and every stripe from then on is computed from a
        /// slot of picture that happened to be bright. Refusing to remove more than
        /// 40% of either axis costs nothing on real letterboxing — 2.39:1 inside 16:9
        /// removes about 25% of the height — and stops the catastrophic case.
        /// </remarks>
        public bool IsPlausibleFor(int sourceWidth, int sourceHeight)
        {
            if (Width <= 0 || Height <= 0 || X < 0 || Y < 0)
            {
                return false;
            }

            if (X + Width > sourceWidth || Y + Height > sourceHeight)
            {
                return false;
            }

            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                return false;
            }

            return Width >= sourceWidth * 0.6 && Height >= sourceHeight * 0.6;
        }
    }
}
