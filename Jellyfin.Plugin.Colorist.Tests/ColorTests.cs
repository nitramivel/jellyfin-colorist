using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Colorist.Core.Color;
using Xunit;

namespace Jellyfin.Plugin.Colorist.Tests
{
    /// <summary>
    /// Frame builders shared across the colour tests.
    /// </summary>
    internal static class Frames
    {
        /// <summary>Builds a frame from a list of (colour, pixel count) pairs.</summary>
        public static byte[] Of(params (Rgb Colour, int Count)[] parts)
        {
            var total = 0;

            foreach (var part in parts)
            {
                total += part.Count;
            }

            var buffer = new byte[total * 3];
            var offset = 0;

            foreach (var (colour, count) in parts)
            {
                for (var i = 0; i < count; i++)
                {
                    buffer[offset++] = colour.R;
                    buffer[offset++] = colour.G;
                    buffer[offset++] = colour.B;
                }
            }

            return buffer;
        }

        public static readonly Rgb Red = new Rgb(220, 30, 30);
        public static readonly Rgb Green = new Rgb(30, 190, 40);
        public static readonly Rgb Grey = new Rgb(120, 120, 122);
        public static readonly Rgb Black = new Rgb(0, 0, 0);
    }

    public class OklabTests
    {
        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(255, 255, 255)]
        [InlineData(220, 30, 30)]
        [InlineData(18, 52, 86)]
        [InlineData(1, 2, 3)]
        public void RoundTripsThroughOklab(byte r, byte g, byte b)
        {
            var result = Oklab.FromSrgb(r, g, b).ToSrgb();

            // One level of tolerance: the conversion runs through a cube root and back
            // in single precision, so demanding exactness would be testing the FPU.
            Assert.InRange(Math.Abs(result.R - r), 0, 1);
            Assert.InRange(Math.Abs(result.G - g), 0, 1);
            Assert.InRange(Math.Abs(result.B - b), 0, 1);
        }

        [Fact]
        public void GreyHasEssentiallyNoChroma()
        {
            Assert.InRange(Oklab.FromSrgb(128, 128, 128).Chroma, 0f, 0.01f);
        }

        [Fact]
        public void SaturatedColourHasMoreChromaThanMutedOneOfSameHue()
        {
            var vivid = Oklab.FromSrgb(220, 20, 20).Chroma;
            var muted = Oklab.FromSrgb(150, 110, 110).Chroma;

            Assert.True(vivid > muted, $"expected vivid {vivid} > muted {muted}");
        }

        [Fact]
        public void DarkNavyIsNotTreatedAsHighlySaturated()
        {
            // The specific failure Oklab chroma is chosen to avoid: HSV calls this
            // fully saturated, which would make every night scene look like it holds
            // a vivid colour.
            var navy = Oklab.FromSrgb(0, 0, 60).Chroma;
            var yellow = Oklab.FromSrgb(255, 230, 0).Chroma;

            Assert.True(navy < yellow, $"expected navy {navy} < yellow {yellow}");
        }
    }

    public class StrategyTests
    {
        public static TheoryData<IFrameColorStrategy> Clustering => new TheoryData<IFrameColorStrategy>
        {
            new MedianCutStrategy(),
            new KMeansStrategy(),
        };

        [Theory]
        [MemberData(nameof(Clustering))]
        public void UniformFrameReturnsThatColour(IFrameColorStrategy strategy)
        {
            var frame = Frames.Of((Frames.Red, 500));
            var result = strategy.Represent(frame, ColorOptions.Default);

            Assert.InRange(Math.Abs(result.R - Frames.Red.R), 0, 6);
            Assert.InRange(Math.Abs(result.G - Frames.Red.G), 0, 6);
            Assert.InRange(Math.Abs(result.B - Frames.Red.B), 0, 6);
        }

        [Theory]
        [MemberData(nameof(Clustering))]
        public void RedAgainstGreenDoesNotProduceMud(IFrameColorStrategy strategy)
        {
            // The motivating case for the whole design. Averaging this frame gives a
            // desaturated brown that appears nowhere in it; clustering must return
            // something recognisably red or green.
            var frame = Frames.Of((Frames.Red, 400), (Frames.Green, 600));

            var clustered = strategy.Represent(frame, ColorOptions.Default).ToOklab();
            var averaged = new MeanStrategy().Represent(frame, ColorOptions.Default).ToOklab();

            Assert.True(
                clustered.Chroma > averaged.Chroma * 1.5f,
                $"clustered chroma {clustered.Chroma} should far exceed averaged {averaged.Chroma}");
        }

        [Fact]
        public void AveragingRedAndGreenReturnsAColourThatIsInNeitherOfThem()
        {
            // Stated as a test so the claim in MeanStrategy's documentation is checked
            // rather than merely asserted. The point is not that the average has low
            // chroma — averaged in linear light it keeps a fair amount — but that it
            // is an olive that appears nowhere in the frame, and is further from both
            // real colours than they are from each other's neighbourhood.
            var frame = Frames.Of((Frames.Red, 500), (Frames.Green, 500));
            var mean = new MeanStrategy().Represent(frame, ColorOptions.Default).ToOklab();

            var toRed = MathF.Sqrt(mean.DistanceSquared(Frames.Red.ToOklab()));
            var toGreen = MathF.Sqrt(mean.DistanceSquared(Frames.Green.ToOklab()));

            Assert.True(toRed > 0.1f, $"average sat too close to red: {toRed}");
            Assert.True(toGreen > 0.1f, $"average sat too close to green: {toGreen}");

            // And the clustering default lands on one of the two real colours instead.
            var clustered = new MedianCutStrategy().Represent(frame, ColorOptions.Default).ToOklab();

            var clusteredNearest = MathF.Min(
                MathF.Sqrt(clustered.DistanceSquared(Frames.Red.ToOklab())),
                MathF.Sqrt(clustered.DistanceSquared(Frames.Green.ToOklab())));

            Assert.True(
                clusteredNearest < MathF.Min(toRed, toGreen),
                $"clustered result ({clusteredNearest}) should sit nearer a real colour than the average ({MathF.Min(toRed, toGreen)})");
        }

        [Theory]
        [MemberData(nameof(Clustering))]
        public void SmallVividRegionBeatsLargeGreyOne(IFrameColorStrategy strategy)
        {
            // 90% dull grey, 10% vivid red. At the default exponent the red should win
            // — this is the behaviour the dominance exponent exists to produce.
            var frame = Frames.Of((Frames.Grey, 900), (Frames.Red, 100));
            var result = strategy.Represent(frame, ColorOptions.Default);

            Assert.True(result.R > result.G + 40, $"expected a reddish result, got {result.ToHex()}");
        }

        [Theory]
        [MemberData(nameof(Clustering))]
        public void AHighExponentHandsTheFrameToTheLargestRegion(IFrameColorStrategy strategy)
        {
            // 90% dull grey against 10% vivid red. Area only overcomes a 24-to-1
            // chroma advantage somewhere above an exponent of 1.4, which is why the
            // knob's range runs to 2 and why 1.0 is not the "area wins" setting.
            var frame = Frames.Of((Frames.Grey, 900), (Frames.Red, 100));
            var result = strategy.Represent(frame, ColorOptions.Default with { DominanceExponent = 2.0f });

            Assert.True(
                Math.Abs(result.R - result.G) < 40,
                $"expected the grey majority to win at exponent 2, got {result.ToHex()}");
        }

        [Theory]
        [MemberData(nameof(Clustering))]
        public void RaisingTheExponentMovesTheAnswerTowardsArea(IFrameColorStrategy strategy)
        {
            // The knob's actual contract: monotonic, not a switch at any one value.
            var frame = Frames.Of((Frames.Grey, 900), (Frames.Red, 100));

            var vivid = strategy.Represent(frame, ColorOptions.Default with { DominanceExponent = 0f });
            var area = strategy.Represent(frame, ColorOptions.Default with { DominanceExponent = 2f });

            Assert.True(vivid.ToOklab().Chroma > area.ToOklab().Chroma,
                $"exponent 0 gave {vivid.ToHex()}, exponent 2 gave {area.ToHex()}");
        }

        [Theory]
        [MemberData(nameof(Clustering))]
        public void FullyBlackFrameStaysBlack(IFrameColorStrategy strategy)
        {
            // Everything falls below the black floor, so the fallback path runs. It
            // must not invent a colour — credits and fades depend on this.
            var frame = Frames.Of((Frames.Black, 400));
            var result = strategy.Represent(frame, ColorOptions.Default);

            Assert.InRange(result.R, 0, 4);
            Assert.InRange(result.G, 0, 4);
            Assert.InRange(result.B, 0, 4);
        }

        [Theory]
        [MemberData(nameof(Clustering))]
        public void LampInADarkRoomReadsAsTheLamp(IFrameColorStrategy strategy)
        {
            var lamp = new Rgb(255, 170, 60);
            var frame = Frames.Of((new Rgb(4, 4, 6), 950), (lamp, 50));

            var result = strategy.Represent(frame, ColorOptions.Default);

            Assert.True(result.R > 120, $"expected the lamp to survive the dark surround, got {result.ToHex()}");
        }

        [Fact]
        public void KMeansIsDeterministic()
        {
            // The property the fixed seed exists for. A barcode that changes between
            // identical runs cannot be cached or compared.
            var frame = Frames.Of(
                (Frames.Red, 137),
                (Frames.Green, 211),
                (Frames.Grey, 89),
                (new Rgb(40, 60, 200), 173));

            var strategy = new KMeansStrategy();
            var first = strategy.Represent(frame, ColorOptions.Default);

            for (var i = 0; i < 8; i++)
            {
                Assert.Equal(first, new KMeansStrategy().Represent(frame, ColorOptions.Default));
            }
        }

        [Theory]
        [MemberData(nameof(Clustering))]
        public void GreyscaleFrameReturnsGreyRatherThanArbitraryColour(IFrameColorStrategy strategy)
        {
            var frame = Frames.Of(
                (new Rgb(40, 40, 40), 300),
                (new Rgb(128, 128, 128), 400),
                (new Rgb(200, 200, 200), 300));

            var result = strategy.Represent(frame, ColorOptions.Default);

            Assert.InRange(Math.Abs(result.R - result.G), 0, 6);
            Assert.InRange(Math.Abs(result.G - result.B), 0, 6);
        }

        [Fact]
        public void FactoryFallsBackRatherThanThrowing()
        {
            Assert.Equal(StrategyFactory.DefaultKey, StrategyFactory.Create("no-such-algorithm").Key);
            Assert.Equal(StrategyFactory.DefaultKey, StrategyFactory.Create(null).Key);
            Assert.Equal(StrategyFactory.DefaultKey, StrategyFactory.Create("  ").Key);
        }

        [Fact]
        public void FactoryResolvesEveryAdvertisedKey()
        {
            foreach (var key in StrategyFactory.Keys)
            {
                Assert.Equal(key, StrategyFactory.Create(key).Key);
            }
        }

        [Theory]
        [MemberData(nameof(Clustering))]
        public void HandlesAFrameWithASinglePixel(IFrameColorStrategy strategy)
        {
            var result = strategy.Represent(Frames.Of((Frames.Green, 1)), ColorOptions.Default);

            Assert.True(result.G > result.R, $"expected green, got {result.ToHex()}");
        }
    }
}
