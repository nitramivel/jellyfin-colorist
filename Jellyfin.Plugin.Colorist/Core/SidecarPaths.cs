using System;
using System.Globalization;
using System.IO;

namespace Jellyfin.Plugin.Colorist.Core
{
    /// <summary>
    /// Works out where an item's barcode file belongs.
    /// </summary>
    /// <remarks>
    /// <b>The suffix is the whole design.</b> Jellyfin's image resolver claims a
    /// fixed set of sidecar names next to a video — <c>-thumb</c>, <c>-poster</c>,
    /// <c>-fanart</c>, <c>-banner</c>, <c>-logo</c>, <c>-clearart</c>, <c>-disc</c>,
    /// <c>-backdrop</c>, <c>-landscape</c> and friends. Writing a file whose name
    /// lands in that set means the next library scan adopts the barcode as the
    /// item's artwork, and a viewer opens their library to find every poster
    /// replaced by a colour strip. <c>-colorist</c> is in none of those sets, so the
    /// scanner walks past it.
    /// </remarks>
    public static class SidecarPaths
    {
        /// <summary>The suffix appended before the extension.</summary>
        public const string Suffix = "-colorist";

        /// <summary>The extension, including the dot.</summary>
        public const string Extension = ".png";

        /// <summary>
        /// The sidecar path for a video, beside the video itself.
        /// </summary>
        /// <param name="mediaPath">Full path to the video file.</param>
        /// <returns>The barcode path, or null when the media path is unusable.</returns>
        /// <remarks>
        /// Derived from the video's own filename rather than from the item's title.
        /// A season folder holds every episode of the season, so a title-derived name
        /// would need the season and episode numbers reconstructed and would collide
        /// the moment two items disagreed about their numbering. The video filename is
        /// already unique within its folder, by definition of being a filename.
        /// </remarks>
        public static string? ForMedia(string? mediaPath)
        {
            if (string.IsNullOrWhiteSpace(mediaPath))
            {
                return null;
            }

            string? directory;
            string stem;

            try
            {
                directory = Path.GetDirectoryName(mediaPath);
                stem = Path.GetFileNameWithoutExtension(mediaPath);
            }
            catch (ArgumentException)
            {
                // Invalid characters in the stored path. A library row can outlive the
                // file it describes and hold something Path refuses to parse.
                return null;
            }

            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(stem))
            {
                return null;
            }

            return Path.Combine(directory, stem + Suffix + Extension);
        }

        /// <summary>
        /// The fallback path under the plugin's own data directory.
        /// </summary>
        /// <param name="dataRoot">The plugin data directory.</param>
        /// <param name="itemId">The Jellyfin item ID.</param>
        /// <returns>The fallback barcode path.</returns>
        /// <remarks>
        /// Used when the library folder cannot be written to, which is the normal
        /// state of affairs for a read-only bind mount — a common and entirely
        /// reasonable way to run a media server. Keyed on the item ID because there is
        /// no folder structure here to make filenames unique.
        /// <para>
        /// Sharded into 256 subdirectories by the first byte of the ID. A single flat
        /// directory holding one file per episode of a large library is tens of
        /// thousands of entries, which is slow to enumerate on every filesystem and
        /// painful on some.
        /// </para>
        /// </remarks>
        public static string ForFallback(string dataRoot, Guid itemId)
        {
            var id = itemId.ToString("N", CultureInfo.InvariantCulture);

            return Path.Combine(dataRoot, "barcodes", id[..2], id + Extension);
        }

        /// <summary>
        /// Whether a filename is one this plugin generated.
        /// </summary>
        /// <param name="path">A file path.</param>
        /// <returns>Whether it carries the barcode suffix.</returns>
        public static bool IsBarcodeFile(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            return Path.GetFileNameWithoutExtension(path)
                .EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
