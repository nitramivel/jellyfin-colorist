using System;

namespace Jellyfin.Plugin.Colorist.Core.Color
{
    /// <summary>
    /// A colour in the Oklab perceptual space, plus conversions to and from sRGB.
    /// </summary>
    /// <remarks>
    /// <b>Why not just cluster in RGB.</b> Euclidean distance in sRGB does not track
    /// perceived difference. Two greens a long way apart in RGB can be
    /// indistinguishable, while a blue and a purple that look plainly different sit
    /// close together. Cluster in RGB and you get clusters that merge colours a
    /// viewer would call different and split colours they would call the same —
    /// which is exactly the judgement this plugin exists to make.
    /// <para>
    /// Oklab is used rather than CIELAB because it is cheap (two matrix multiplies
    /// and three cube roots, no white-point bookkeeping) and because it does not
    /// have CIELAB's blue-hue problem, where interpolating toward blue swings
    /// visibly purple. Both matter here: the first because this runs over every
    /// sampled frame of every item in a library, the second because night
    /// exteriors and blue-graded footage are a large fraction of what gets sampled.
    /// </para>
    /// <para>
    /// Constants are Björn Ottosson's published sRGB-to-Oklab matrices.
    /// </para>
    /// </remarks>
    public readonly struct Oklab : IEquatable<Oklab>
    {
        /// <summary>Initialises a new instance of the <see cref="Oklab"/> struct.</summary>
        /// <param name="l">Perceived lightness, 0 to 1.</param>
        /// <param name="a">Green/red axis.</param>
        /// <param name="b">Blue/yellow axis.</param>
        public Oklab(float l, float a, float b)
        {
            L = l;
            A = a;
            B = b;
        }

        /// <summary>Gets the perceived lightness, roughly 0 to 1.</summary>
        public float L { get; }

        /// <summary>Gets the green/red axis.</summary>
        public float A { get; }

        /// <summary>Gets the blue/yellow axis.</summary>
        public float B { get; }

        /// <summary>
        /// Gets the chroma — distance from the neutral axis.
        /// </summary>
        /// <remarks>
        /// This is the "how colourful is it" term the strategies score on, and it is
        /// the reason the conversion is worth doing at all. HSV's saturation is
        /// confounded by brightness: it calls pure yellow and near-black navy both
        /// "fully saturated", so scoring on it makes every dark frame look like it
        /// contains a vivid colour. Oklab chroma does not have that failure.
        /// </remarks>
        public float Chroma => MathF.Sqrt((A * A) + (B * B));

        /// <summary>Converts an 8-bit sRGB triplet to Oklab.</summary>
        /// <param name="r">Red, 0-255.</param>
        /// <param name="g">Green, 0-255.</param>
        /// <param name="b">Blue, 0-255.</param>
        /// <returns>The colour in Oklab.</returns>
        public static Oklab FromSrgb(byte r, byte g, byte b)
        {
            var lr = ToLinear(r);
            var lg = ToLinear(g);
            var lb = ToLinear(b);

            var l = (0.4122214708f * lr) + (0.5363325363f * lg) + (0.0514459929f * lb);
            var m = (0.2119034982f * lr) + (0.6806995451f * lg) + (0.1073969566f * lb);
            var s = (0.0883024619f * lr) + (0.2817188376f * lg) + (0.6299787005f * lb);

            var l_ = MathF.Cbrt(l);
            var m_ = MathF.Cbrt(m);
            var s_ = MathF.Cbrt(s);

            return new Oklab(
                (0.2104542553f * l_) + (0.7936177850f * m_) - (0.0040720468f * s_),
                (1.9779984951f * l_) - (2.4285922050f * m_) + (0.4505937099f * s_),
                (0.0259040371f * l_) + (0.7827717662f * m_) - (0.8086757660f * s_));
        }

        /// <summary>Converts back to an 8-bit sRGB triplet, clamped to gamut.</summary>
        /// <returns>The colour as sRGB.</returns>
        public Rgb ToSrgb()
        {
            var l_ = L + (0.3963377774f * A) + (0.2158037573f * B);
            var m_ = L - (0.1055613458f * A) - (0.0638541728f * B);
            var s_ = L - (0.0894841775f * A) - (1.2914855480f * B);

            var l = l_ * l_ * l_;
            var m = m_ * m_ * m_;
            var s = s_ * s_ * s_;

            return new Rgb(
                FromLinear((4.0767416621f * l) - (3.3077115913f * m) + (0.2309699292f * s)),
                FromLinear((-1.2684380046f * l) + (2.6097574011f * m) - (0.3413193965f * s)),
                FromLinear((-0.0041960863f * l) - (0.7034186147f * m) + (1.7076147010f * s)));
        }

        /// <summary>Squared distance to another colour. Squared because the only use is comparison.</summary>
        /// <param name="other">The colour to measure against.</param>
        /// <returns>The squared Euclidean distance in Oklab.</returns>
        public float DistanceSquared(Oklab other)
        {
            var dl = L - other.L;
            var da = A - other.A;
            var db = B - other.B;
            return (dl * dl) + (da * da) + (db * db);
        }

        /// <inheritdoc />
        public bool Equals(Oklab other) =>
            L.Equals(other.L) && A.Equals(other.A) && B.Equals(other.B);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is Oklab other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(L, A, B);

        /// <summary>Equality operator.</summary>
        /// <param name="left">Left operand.</param>
        /// <param name="right">Right operand.</param>
        /// <returns>Whether the two are equal.</returns>
        public static bool operator ==(Oklab left, Oklab right) => left.Equals(right);

        /// <summary>Inequality operator.</summary>
        /// <param name="left">Left operand.</param>
        /// <param name="right">Right operand.</param>
        /// <returns>Whether the two differ.</returns>
        public static bool operator !=(Oklab left, Oklab right) => !left.Equals(right);

        private static float ToLinear(byte channel)
        {
            var c = channel / 255f;
            return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        private static byte FromLinear(float c)
        {
            var v = c <= 0.0031308f ? c * 12.92f : (1.055f * MathF.Pow(c, 1f / 2.4f)) - 0.055f;
            return (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        }
    }
}
