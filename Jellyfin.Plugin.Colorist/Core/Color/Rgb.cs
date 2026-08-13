using System;
using System.Globalization;

namespace Jellyfin.Plugin.Colorist.Core.Color
{
    /// <summary>An 8-bit sRGB colour.</summary>
    public readonly struct Rgb : IEquatable<Rgb>
    {
        /// <summary>Initialises a new instance of the <see cref="Rgb"/> struct.</summary>
        /// <param name="r">Red, 0-255.</param>
        /// <param name="g">Green, 0-255.</param>
        /// <param name="b">Blue, 0-255.</param>
        public Rgb(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }

        /// <summary>Gets the red channel.</summary>
        public byte R { get; }

        /// <summary>Gets the green channel.</summary>
        public byte G { get; }

        /// <summary>Gets the blue channel.</summary>
        public byte B { get; }

        /// <summary>Gets black.</summary>
        public static Rgb Black => new Rgb(0, 0, 0);

        /// <summary>Converts to Oklab.</summary>
        /// <returns>The same colour in Oklab.</returns>
        public Oklab ToOklab() => Oklab.FromSrgb(R, G, B);

        /// <summary>
        /// Undoes the sRGB transfer function, giving light rather than an encoded byte.
        /// </summary>
        /// <param name="channel">One encoded channel, 0-255.</param>
        /// <returns>The channel as linear light, 0-1.</returns>
        /// <remarks>
        /// Public because averaging colours needs it and Oklab does not answer that
        /// question. Oklab is built for perceptual <i>distance</i> — a straight line
        /// between two colours looking straight — which is why interpolation and
        /// clustering work in it. A mean is a different operation: it combines light,
        /// and light adds linearly. Averaging encoded bytes instead is the classic
        /// image-downscaling bug, and it darkens: mid-grey between black and white
        /// comes out at 128 rather than the 188 that actually reflects half the light.
        /// </remarks>
        public static float ToLinear(byte channel)
        {
            var c = channel / 255f;
            return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        /// <summary>Applies the sRGB transfer function, giving an encoded byte.</summary>
        /// <param name="linear">One channel as linear light.</param>
        /// <returns>The channel encoded, 0-255.</returns>
        public static byte FromLinear(float linear)
        {
            var v = linear <= 0.0031308f
                ? linear * 12.92f
                : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f;

            return (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        }

        /// <summary>Renders as a CSS hex string, for logs and tests.</summary>
        /// <returns>The colour as <c>#rrggbb</c>.</returns>
        public string ToHex() => string.Create(
            CultureInfo.InvariantCulture,
            $"#{R:x2}{G:x2}{B:x2}");

        /// <inheritdoc />
        public bool Equals(Rgb other) => R == other.R && G == other.G && B == other.B;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is Rgb other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(R, G, B);

        /// <inheritdoc />
        public override string ToString() => ToHex();

        /// <summary>Equality operator.</summary>
        /// <param name="left">Left operand.</param>
        /// <param name="right">Right operand.</param>
        /// <returns>Whether the two are equal.</returns>
        public static bool operator ==(Rgb left, Rgb right) => left.Equals(right);

        /// <summary>Inequality operator.</summary>
        /// <param name="left">Left operand.</param>
        /// <param name="right">Right operand.</param>
        /// <returns>Whether the two differ.</returns>
        public static bool operator !=(Rgb left, Rgb right) => !left.Equals(right);
    }
}
