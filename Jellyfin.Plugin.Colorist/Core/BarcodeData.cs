using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Colorist.Core.Color;

namespace Jellyfin.Plugin.Colorist.Core
{
    /// <summary>
    /// The on-disk and over-the-wire form of a barcode: the sampled colours
    /// themselves, not a picture of them.
    /// </summary>
    /// <remarks>
    /// <b>Why the colours and not a PNG.</b> Everything downstream of sampling —
    /// stripe width, blending, how tall the strip is drawn — is a display decision,
    /// and baking those into an image means changing any of them costs another
    /// library-wide ffmpeg run. Storing what was measured makes them free.
    /// <para>
    /// One packed hex string rather than an array of <c>"#rrggbb"</c>. A thousand
    /// columns is 6 KB packed against roughly 10 KB as JSON strings, the column
    /// count falls out of the length, and there is no per-element quoting for a
    /// parser to get through. The wrapper object exists only to carry
    /// <see cref="Version"/>; a bare string would leave no room to change the
    /// encoding later without guessing at what an old file meant.
    /// </para>
    /// </remarks>
    public static class BarcodeData
    {
        /// <summary>The format version written into every document.</summary>
        public const int Version = 1;

        /// <summary>The media type the document is served as.</summary>
        public const string MediaType = "application/json";

        private const string HexDigits = "0123456789abcdef";

        /// <summary>Renders the colours as a complete document.</summary>
        /// <param name="columns">One colour per stripe, in playback order.</param>
        /// <returns>The JSON text to store.</returns>
        /// <exception cref="ArgumentException">No columns were supplied.</exception>
        public static string Serialize(IReadOnlyList<Rgb> columns)
        {
            ArgumentNullException.ThrowIfNull(columns);

            if (columns.Count == 0)
            {
                throw new ArgumentException("A barcode needs at least one sample.", nameof(columns));
            }

            // Hand-written rather than serialised from a type. The document is two
            // fields, one of which is already a string this class produced, so a
            // serialiser would only add a dependency on its own naming policy —
            // and the client reads these keys by name.
            return "{\"v\":"
                + Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"colors\":\""
                + Pack(columns)
                + "\"}";
        }

        /// <summary>Packs colours into a lowercase hex string, six characters each.</summary>
        /// <param name="columns">The colours.</param>
        /// <returns>The packed string.</returns>
        public static string Pack(IReadOnlyList<Rgb> columns)
        {
            ArgumentNullException.ThrowIfNull(columns);

            return string.Create(columns.Count * 6, columns, static (span, source) =>
            {
                for (var i = 0; i < source.Count; i++)
                {
                    var colour = source[i];
                    var at = i * 6;

                    span[at + 0] = HexDigits[colour.R >> 4];
                    span[at + 1] = HexDigits[colour.R & 0xF];
                    span[at + 2] = HexDigits[colour.G >> 4];
                    span[at + 3] = HexDigits[colour.G & 0xF];
                    span[at + 4] = HexDigits[colour.B >> 4];
                    span[at + 5] = HexDigits[colour.B & 0xF];
                }
            });
        }

        /// <summary>Reads a packed string back into colours.</summary>
        /// <param name="packed">The packed hex string.</param>
        /// <returns>The colours, or null when the string is not a whole number of colours.</returns>
        public static IReadOnlyList<Rgb>? Unpack(string? packed)
        {
            if (string.IsNullOrEmpty(packed) || packed.Length % 6 != 0)
            {
                return null;
            }

            var columns = new Rgb[packed.Length / 6];

            for (var i = 0; i < columns.Length; i++)
            {
                var at = i * 6;

                var r = Byte(packed[at + 0], packed[at + 1]);
                var g = Byte(packed[at + 2], packed[at + 3]);
                var b = Byte(packed[at + 4], packed[at + 5]);

                if (r < 0 || g < 0 || b < 0)
                {
                    return null;
                }

                columns[i] = new Rgb((byte)r, (byte)g, (byte)b);
            }

            return columns;
        }

        /// <summary>Reads the colours out of a stored document.</summary>
        /// <param name="json">The document text.</param>
        /// <returns>The colours, or null when the document is unusable.</returns>
        /// <remarks>
        /// Tolerant by design. These files sit in library folders that people back
        /// up, sync and edit, and the only sensible response to one that has been
        /// damaged is to treat the item as not yet generated — which a null does,
        /// because that is what a missing file produces too.
        /// </remarks>
        public static IReadOnlyList<Rgb>? Deserialize(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (!root.TryGetProperty("v", out var version)
                    || version.ValueKind != JsonValueKind.Number
                    || version.GetInt32() != Version)
                {
                    return null;
                }

                return root.TryGetProperty("colors", out var colours)
                    && colours.ValueKind == JsonValueKind.String
                        ? Unpack(colours.GetString())
                        : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static int Byte(char high, char low)
        {
            var h = Nibble(high);
            var l = Nibble(low);

            return h < 0 || l < 0 ? -1 : (h << 4) | l;
        }

        private static int Nibble(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
    }
}
