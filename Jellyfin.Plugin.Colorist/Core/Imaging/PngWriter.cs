using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Jellyfin.Plugin.Colorist.Core.Imaging
{
    /// <summary>
    /// Writes a truecolour PNG. No dependencies.
    /// </summary>
    /// <remarks>
    /// <b>Why hand-rolled rather than an imaging library.</b> Jellyfin's own SkiaSharp
    /// lives in <c>Jellyfin.Drawing.Skia</c>, a server assembly that is not published
    /// for plugins, so using it would mean shipping the package and its native
    /// <c>libSkiaSharp</c> per architecture into a process that already has its own
    /// copy loaded. That is a real risk to take on for an image consisting of solid
    /// rectangles. ffmpeg hands us raw rgb24 and PNG's own format is a header, a
    /// deflate stream and a CRC — so there is nothing left for a general imaging
    /// library to do here.
    /// <para>
    /// The one piece of good fortune is that PNG mandates the zlib wrapper around
    /// its deflate data, which is exactly what <see cref="ZLibStream"/> emits.
    /// <see cref="DeflateStream"/> would produce a raw stream and every decoder on
    /// earth would reject the file.
    /// </para>
    /// </remarks>
    public static class PngWriter
    {
        private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        /// <summary>Encodes a tightly-packed rgb24 buffer as a PNG.</summary>
        /// <param name="rgb24">Pixels, row-major, three bytes each, length exactly width × height × 3.</param>
        /// <param name="width">Image width in pixels.</param>
        /// <param name="height">Image height in pixels.</param>
        /// <returns>The complete PNG file.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Dimensions are not positive.</exception>
        /// <exception cref="ArgumentException">The buffer length does not match the dimensions.</exception>
        public static byte[] Encode(ReadOnlySpan<byte> rgb24, int width, int height)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

            var expected = (long)width * height * 3;

            if (rgb24.Length != expected)
            {
                throw new ArgumentException(
                    $"Expected {expected} bytes for {width}x{height}, got {rgb24.Length}.",
                    nameof(rgb24));
            }

            using var output = new MemoryStream();
            output.Write(Signature);

            Span<byte> header = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header[..4], width);
            BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), height);
            header[8] = 8;  // bit depth
            header[9] = 2;  // colour type 2: truecolour RGB
            header[10] = 0; // deflate
            header[11] = 0; // adaptive filtering
            header[12] = 0; // no interlace

            WriteChunk(output, "IHDR", header);
            WriteChunk(output, "IDAT", Compress(rgb24, width, height));
            WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);

            return output.ToArray();
        }

        /// <summary>
        /// Filters and deflates the scanlines.
        /// </summary>
        /// <remarks>
        /// Every row is written with filter type 0 (None). PNG offers four predictive
        /// filters that pay off on photographic data, but this image is columns of
        /// flat colour: horizontally it is long runs of identical bytes, which deflate
        /// already collapses to nearly nothing, and vertically every row is identical
        /// to the one above, which deflate collapses even harder. Filtering would add
        /// a per-row heuristic and buy essentially no bytes on this particular
        /// content.
        /// </remarks>
        private static byte[] Compress(ReadOnlySpan<byte> rgb24, int width, int height)
        {
            var stride = width * 3;

            using var compressed = new MemoryStream();

            // Left in the using block and disposed before ToArray: ZLibStream writes
            // its trailing Adler-32 checksum on dispose, and reading the buffer while
            // it is still open yields a stream that ends mid-thought.
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (var y = 0; y < height; y++)
                {
                    zlib.WriteByte(0);
                    zlib.Write(rgb24.Slice(y * stride, stride));
                }
            }

            return compressed.ToArray();
        }

        private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            output.Write(length);

            var typeBytes = Encoding.ASCII.GetBytes(type);
            output.Write(typeBytes);
            output.Write(data);

            // The CRC covers the type and the data but not the length field.
            var crc = Crc32.Compute(typeBytes, data);
            Span<byte> crcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            output.Write(crcBytes);
        }
    }
}
