using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Colorist.Core.Color;
using Jellyfin.Plugin.Colorist.Core.Imaging;
using Xunit;

namespace Jellyfin.Plugin.Colorist.Tests
{
    /// <summary>
    /// A minimal PNG reader, so the writer is checked against an independent
    /// implementation of the format rather than against itself.
    /// </summary>
    internal static class TinyPngReader
    {
        public static (int Width, int Height, byte[] Rgb) Decode(byte[] png)
        {
            Assert.True(png.Length > 8, "file is too short to be a PNG");

            var signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            Assert.Equal(signature, png.Take(8).ToArray());

            var offset = 8;
            var width = 0;
            var height = 0;
            var idat = new MemoryStream();
            var sawEnd = false;

            while (offset + 8 <= png.Length)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
                var type = Encoding.ASCII.GetString(png, offset + 4, 4);
                var dataStart = offset + 8;

                Assert.True(dataStart + length + 4 <= png.Length, $"chunk {type} runs past the end of the file");

                // Every chunk's CRC is verified. A wrong CRC is the single most likely
                // way a hand-written encoder produces a file that some decoders accept
                // and others reject.
                var expected = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(dataStart + length, 4));
                var actual = Crc(png.AsSpan(offset + 4, 4 + length));
                Assert.True(expected == actual, $"CRC mismatch on chunk {type}");

                switch (type)
                {
                    case "IHDR":
                        width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(dataStart, 4));
                        height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(dataStart + 4, 4));
                        Assert.Equal(8, png[dataStart + 8]);
                        Assert.Equal(2, png[dataStart + 9]);
                        break;
                    case "IDAT":
                        idat.Write(png, dataStart, length);
                        break;
                    case "IEND":
                        sawEnd = true;
                        break;
                    default:
                        break;
                }

                offset = dataStart + length + 4;
            }

            Assert.True(sawEnd, "no IEND chunk");
            Assert.True(width > 0 && height > 0, "no IHDR chunk");

            idat.Position = 0;
            using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
            using var raw = new MemoryStream();
            zlib.CopyTo(raw);

            var stride = width * 3;
            var bytes = raw.ToArray();
            Assert.Equal((stride + 1) * height, bytes.Length);

            var pixels = new byte[stride * height];

            for (var y = 0; y < height; y++)
            {
                Assert.Equal(0, bytes[y * (stride + 1)]);
                Array.Copy(bytes, (y * (stride + 1)) + 1, pixels, y * stride, stride);
            }

            return (width, height, pixels);
        }

        private static uint Crc(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;

            foreach (var b in data)
            {
                var c = (crc ^ b) & 0xFF;

                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                crc = c ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }
    }

    public class PngWriterTests
    {
        [Fact]
        public void ProducesAFileAnIndependentReaderAccepts()
        {
            var pixels = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0 };
            var png = PngWriter.Encode(pixels, 2, 2);

            var (width, height, decoded) = TinyPngReader.Decode(png);

            Assert.Equal(2, width);
            Assert.Equal(2, height);
            Assert.Equal(pixels, decoded);
        }

        [Fact]
        public void RoundTripsALargerImageExactly()
        {
            const int Width = 313;
            const int Height = 47;

            var random = new Random(7);
            var pixels = new byte[Width * Height * 3];
            random.NextBytes(pixels);

            var (width, height, decoded) = TinyPngReader.Decode(PngWriter.Encode(pixels, Width, Height));

            Assert.Equal(Width, width);
            Assert.Equal(Height, height);
            Assert.Equal(pixels, decoded);
        }

        [Fact]
        public void UsesZlibFramingRatherThanRawDeflate()
        {
            // The specific mistake worth a test of its own: DeflateStream would emit a
            // stream that is byte-for-byte plausible and that no PNG decoder accepts.
            // The first IDAT byte must be a zlib CMF with compression method 8.
            var png = PngWriter.Encode(new byte[] { 1, 2, 3 }, 1, 1);
            var marker = Encoding.ASCII.GetBytes("IDAT");

            var index = -1;
            for (var i = 0; i + marker.Length <= png.Length; i++)
            {
                if (png.AsSpan(i, marker.Length).SequenceEqual(marker))
                {
                    index = i;
                    break;
                }
            }

            Assert.True(index > 0, "no IDAT chunk found");
            Assert.Equal(8, png[index + 4] & 0x0F);
        }

        [Fact]
        public void RejectsMismatchedBufferLength()
        {
            Assert.Throws<ArgumentException>(() => PngWriter.Encode(new byte[10], 4, 4));
        }

        [Theory]
        [InlineData(0, 4)]
        [InlineData(4, 0)]
        [InlineData(-1, 4)]
        public void RejectsNonPositiveDimensions(int width, int height)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => PngWriter.Encode(new byte[Math.Max(0, width * height * 3)], width, height));
        }
    }

    public class BarcodeComposerTests
    {
        private static readonly Rgb Red = new Rgb(255, 0, 0);
        private static readonly Rgb Blue = new Rgb(0, 0, 255);

        [Fact]
        public void EveryRowIsIdentical()
        {
            var pixels = BarcodeComposer.Compose([Red, Blue], 8, 5, smooth: false);
            var stride = 8 * 3;

            for (var y = 1; y < 5; y++)
            {
                Assert.Equal(
                    pixels.AsSpan(0, stride).ToArray(),
                    pixels.AsSpan(y * stride, stride).ToArray());
            }
        }

        [Fact]
        public void UnsmoothedColumnsStayExact()
        {
            var pixels = BarcodeComposer.Compose([Red, Blue], 4, 1, smooth: false);

            Assert.Equal(new byte[] { 255, 0, 0 }, pixels[0..3]);
            Assert.Equal(new byte[] { 255, 0, 0 }, pixels[3..6]);
            Assert.Equal(new byte[] { 0, 0, 255 }, pixels[6..9]);
            Assert.Equal(new byte[] { 0, 0, 255 }, pixels[9..12]);
        }

        [Fact]
        public void SmoothingNeverDipsDarkerThanEitherEndpoint()
        {
            // The seam this guards against: blending red to blue in sRGB passes
            // through #800080, which is perceptually darker than either end, so a
            // smoothed strip picks up a dark band at every transition. Interpolating
            // in Oklab cannot go below the darker endpoint, because lightness moves
            // linearly between them.
            //
            // Note the floor is blue's lightness, not red's — pure blue really is much
            // darker than pure red in Oklab (0.45 against 0.63), and that is a
            // property of the colours, not a defect in the blend.
            var pixels = BarcodeComposer.Compose([Red, Blue], 64, 1, smooth: true);

            var floor = MathF.Min(Red.ToOklab().L, Blue.ToOklab().L);
            var naiveMidpoint = new Rgb(128, 0, 128).ToOklab().L;

            var darkest = float.MaxValue;

            for (var x = 0; x < 64; x++)
            {
                var l = new Rgb(pixels[x * 3], pixels[(x * 3) + 1], pixels[(x * 3) + 2]).ToOklab().L;
                darkest = MathF.Min(darkest, l);
            }

            Assert.True(
                darkest >= floor - 0.02f,
                $"darkest stripe {darkest} fell below the darker endpoint {floor}");

            Assert.True(
                darkest > naiveMidpoint,
                $"Oklab blending ({darkest}) should stay lighter than naive sRGB blending ({naiveMidpoint})");
        }

        [Fact]
        public void BandsAreIgnoredWhenNotSmoothing()
        {
            // Reducing and then banding would draw a handful of wide blocks, which is
            // the one combination nothing wants, so the samples stay exact.
            var pixels = BarcodeComposer.Compose([Red, Blue], 4, 1, smooth: false, bands: 2);

            Assert.Equal(new byte[] { 255, 0, 0 }, pixels[0..3]);
            Assert.Equal(new byte[] { 0, 0, 255 }, pixels[9..12]);
        }

        [Fact]
        public void ReducingToBandsFlattensDetailTheBlendKeeps()
        {
            // The difference between the two smooth styles, and the whole point of the
            // gradient: alternating red and blue every sample is a strip that swings
            // between them, and averaging it down to two bands is a strip that barely
            // moves. Measured as the total change from pixel to pixel, which is high
            // for the blend and near zero once the detail has been averaged away.
            var alternating = new Rgb[64];

            for (var i = 0; i < alternating.Length; i++)
            {
                alternating[i] = i % 2 == 0 ? Red : Blue;
            }

            var blended = BarcodeComposer.Compose(alternating, 256, 1, smooth: true);
            var gradient = BarcodeComposer.Compose(alternating, 256, 1, smooth: true, bands: 2);

            Assert.True(
                Variation(gradient, 256) < Variation(blended, 256) / 10,
                $"gradient variation {Variation(gradient, 256)} should be far below "
                + $"blended {Variation(blended, 256)}");
        }

        private static int Variation(byte[] pixels, int width)
        {
            var total = 0;

            for (var x = 1; x < width; x++)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    total += Math.Abs(pixels[(x * 3) + channel] - pixels[((x - 1) * 3) + channel]);
                }
            }

            return total;
        }

        [Fact]
        public void SingleColumnFillsTheWholeImage()
        {
            var pixels = BarcodeComposer.Compose([Red], 5, 2, smooth: true);

            for (var i = 0; i < pixels.Length; i += 3)
            {
                Assert.Equal(255, pixels[i]);
                Assert.Equal(0, pixels[i + 1]);
            }
        }

        [Fact]
        public void MoreColumnsThanPixelsStillFills()
        {
            var columns = new List<Rgb>();

            for (var i = 0; i < 500; i++)
            {
                columns.Add(i % 2 == 0 ? Red : Blue);
            }

            var pixels = BarcodeComposer.Compose(columns, 10, 1, smooth: false);

            Assert.Equal(30, pixels.Length);
        }

        [Fact]
        public void RejectsAnEmptyColumnList()
        {
            Assert.Throws<ArgumentException>(
                () => BarcodeComposer.Compose(Array.Empty<Rgb>(), 4, 4, smooth: false));
        }

        [Fact]
        public void ComposedOutputIsExactlyTheSizePngWriterExpects()
        {
            // These two are always used together, so the contract between them is
            // worth pinning: a mismatch would throw at generation time on a live
            // server rather than here.
            var pixels = BarcodeComposer.Compose([Red, Blue], 37, 11, smooth: true);

            var (width, height, _) = TinyPngReader.Decode(PngWriter.Encode(pixels, 37, 11));

            Assert.Equal(37, width);
            Assert.Equal(11, height);
        }
    }
}
