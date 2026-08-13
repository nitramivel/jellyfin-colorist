using System;
using System.Text;
using Jellyfin.Plugin.Colorist.Configuration;
using Jellyfin.Plugin.Colorist.Core.Color;
using Jellyfin.Plugin.Colorist.Core.Imaging;
using Xunit;

namespace Jellyfin.Plugin.Colorist.Tests
{
    public class ColourBandsTests
    {
        private static readonly Rgb Black = new Rgb(0, 0, 0);
        private static readonly Rgb White = new Rgb(255, 255, 255);

        [Fact]
        public void AveragingHappensInLinearLight()
        {
            // The whole reason this is not a byte mean. Half black and half white is
            // half the light, which encodes to 188 — a byte mean gives 128, which is
            // the classic downscaling bug and visibly darkens a gradient built from it.
            var reduced = ColourBands.Reduce([Black, White], 1);

            Assert.Single(reduced);
            Assert.InRange(reduced[0].R, 186, 190);
            Assert.Equal(reduced[0].R, reduced[0].G);
            Assert.Equal(reduced[0].R, reduced[0].B);
        }

        [Fact]
        public void EverySampleIsCountedExactlyOnce()
        {
            // Boundaries derived from the band index rather than accumulated, so a
            // count that does not divide evenly neither drops the last samples nor
            // counts any twice. Seven greys into two bands: 0,1,2 and 3,4,5,6.
            var greys = new Rgb[7];

            for (var i = 0; i < greys.Length; i++)
            {
                greys[i] = new Rgb((byte)(i * 40), (byte)(i * 40), (byte)(i * 40));
            }

            var reduced = ColourBands.Reduce(greys, 2);

            Assert.Equal(2, reduced.Count);
            Assert.Equal(Mean(greys, 0, 3).R, reduced[0].R);
            Assert.Equal(Mean(greys, 3, 7).R, reduced[1].R);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(4)]
        [InlineData(99)]
        public void AskingForAsManyBandsAsThereAreSamplesChangesNothing(int bands)
        {
            // Not an error, and not worth stretching: a 90-second video sampled 16
            // times would otherwise interpolate detail it never had.
            var columns = new[] { Black, White, Black, White };

            Assert.Same(columns, ColourBands.Reduce(columns, bands));
        }

        [Fact]
        public void ASingleBandIsTheMeanOfEverything()
        {
            var reduced = ColourBands.Reduce([Black, White, Black, White], 1);

            Assert.Single(reduced);
            Assert.Equal(Mean([Black, White, Black, White], 0, 4).R, reduced[0].R);
        }

        [Fact]
        public void TheSequenceKeepsItsDirection()
        {
            // A gradient is only the film's arc if the arc survives: a run that darkens
            // must reduce to bands that darken, in that order.
            var fading = new Rgb[120];

            for (var i = 0; i < fading.Length; i++)
            {
                var level = (byte)(255 - (i * 2));
                fading[i] = new Rgb(level, level, level);
            }

            var reduced = ColourBands.Reduce(fading, 8);

            for (var i = 1; i < reduced.Count; i++)
            {
                Assert.True(
                    reduced[i].R < reduced[i - 1].R,
                    $"band {i} ({reduced[i].R}) should be darker than {i - 1} ({reduced[i - 1].R})");
            }
        }

        [Fact]
        public void NoColumnsIsRejectedRatherThanGuessed()
        {
            Assert.Throws<ArgumentNullException>(() => ColourBands.Reduce(null!, 4));
        }

        [Theory]
        [InlineData(2, "959797989797")]
        [InlineData(
            16,
            "8696959a9496a195989096999198949f9f959c94988b9399999498a199968d9a"
            + "97959b9a9e93979a95988e96979b9897")]
        public void TheClientReducesToTheSameColoursThisDoes(int bands, string expected)
        {
            // Parity with reduceToBands in colorist.js, which is the claim that lets the
            // detail page and the optional PNG be called the same picture. These strings
            // were produced by running that function on this input under Node, not by
            // running this one — so a change to either side that drifts from the other
            // fails here rather than showing up as a strip that does not match its own
            // PNG. The input is (i*7, i*13, i*29) mod 256 because both languages
            // reproduce it exactly: no multiplication large enough to lose precision in
            // a double, which a random generator seeded the same way would have hit.
            var input = new Rgb[1000];

            for (var i = 0; i < input.Length; i++)
            {
                input[i] = new Rgb(
                    (byte)((i * 7) % 256),
                    (byte)((i * 13) % 256),
                    (byte)((i * 29) % 256));
            }

            var actual = new StringBuilder();

            foreach (var colour in ColourBands.Reduce(input, bands))
            {
                actual.Append(colour.ToHex().AsSpan(1));
            }

            Assert.Equal(expected, actual.ToString());
        }

        private static Rgb Mean(Rgb[] columns, int from, int to)
        {
            double r = 0, g = 0, b = 0;

            for (var i = from; i < to; i++)
            {
                r += Rgb.ToLinear(columns[i].R);
                g += Rgb.ToLinear(columns[i].G);
                b += Rgb.ToLinear(columns[i].B);
            }

            var count = to - from;

            return new Rgb(
                Rgb.FromLinear((float)(r / count)),
                Rgb.FromLinear((float)(g / count)),
                Rgb.FromLinear((float)(b / count)));
        }
    }

    public class BarcodeStyleUpgradeTests
    {
        [Fact]
        public void AServerThatHadBlendingOnKeepsIt()
        {
            // The migration that matters. Every configuration written before 0.4.0.0
            // has no Style element at all, so reading its absence as the enum's zero
            // value would silently turn blending off for everyone who had chosen it.
            var configuration = new PluginConfiguration { Smooth = true, Style = null };

            Assert.Equal(BarcodeStyle.Blended, configuration.ResolveStyle());
        }

        [Fact]
        public void AServerThatHadBlendingOffKeepsThatToo()
        {
            var configuration = new PluginConfiguration { Smooth = false, Style = null };

            Assert.Equal(BarcodeStyle.Stripes, configuration.ResolveStyle());
        }

        [Theory]
        [InlineData(BarcodeStyle.Stripes)]
        [InlineData(BarcodeStyle.Blended)]
        [InlineData(BarcodeStyle.Gradient)]
        public void AnExplicitChoiceBeatsTheOldBoolean(BarcodeStyle style)
        {
            // Including Stripes against Smooth = true, which is the case a
            // non-nullable enum could not express: somebody deliberately turning
            // blending off has to stick.
            var configuration = new PluginConfiguration { Smooth = true, Style = style };

            Assert.Equal(style, configuration.ResolveStyle());
        }

        [Fact]
        public void AFreshInstallDrawsHardStripes()
        {
            Assert.Equal(BarcodeStyle.Stripes, new PluginConfiguration().ResolveStyle());
        }

        [Theory]
        [InlineData(0, 2)]
        [InlineData(1, 2)]
        [InlineData(-40, 2)]
        [InlineData(16, 16)]
        [InlineData(99999, 4000)]
        public void TheBandCountIsClampedRatherThanTrusted(int configured, int expected)
        {
            // Read from an XML file a hand edit or a failed upgrade can leave holding
            // anything; a band count of zero would divide by nothing.
            var configuration = new PluginConfiguration { GradientBands = configured };

            Assert.Equal(expected, configuration.ResolveGradientBands());
        }
    }
}
