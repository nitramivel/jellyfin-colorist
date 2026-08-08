using System;

namespace Jellyfin.Plugin.Colorist.Core.Imaging
{
    /// <summary>
    /// The CRC-32 every PNG chunk is required to carry.
    /// </summary>
    /// <remarks>
    /// .NET ships <c>System.IO.Hashing.Crc32</c>, but only in a separate NuGet
    /// package, and the whole argument for writing the PNG by hand was to add no
    /// dependencies. This is the standard table-driven implementation from the PNG
    /// specification's own appendix, at about thirty lines.
    /// </remarks>
    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        /// <summary>Computes the CRC over a chunk's type and data.</summary>
        /// <param name="type">The four-byte chunk type.</param>
        /// <param name="data">The chunk payload.</param>
        /// <returns>The CRC-32.</returns>
        public static uint Compute(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;

            foreach (var b in type)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];

            for (var n = 0u; n < 256u; n++)
            {
                var c = n;

                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}
