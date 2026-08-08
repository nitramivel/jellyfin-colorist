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
