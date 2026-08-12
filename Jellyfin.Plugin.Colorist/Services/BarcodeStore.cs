using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Colorist.Core;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Colorist.Services
{
    /// <summary>Where a barcode ended up.</summary>
    /// <param name="Path">Full path to the written colour data.</param>
    /// <param name="BesideMedia">Whether it landed next to the media rather than in plugin data.</param>
    public readonly record struct StoredBarcode(string Path, bool BesideMedia);

    /// <summary>
    /// Reads and writes barcode files.
    /// </summary>
    /// <remarks>
    /// <b>Two locations, one lookup.</b> The preferred home is beside the media, so
    /// the barcode travels with the file and is visible to anything else that looks
    /// in that folder. The fallback is the plugin's data directory, for the very
    /// common case of a library mounted read-only. Everything that reads a barcode
    /// asks this class, which checks both — so nothing downstream, least of all the
    /// web client, has to know or care which one an item used.
    /// <para>
    /// <b>Two files, one decision.</b> The colour data is the barcode; the PNG is an
    /// optional rendering of it. They are written and removed together and neither
    /// is allowed to land in a different location from the other, because an item
    /// whose data says one thing and whose picture says another is worse than an
    /// item with no picture.
    /// </para>
    /// </remarks>
    public sealed class BarcodeStore
    {
        private readonly IApplicationPaths _paths;
        private readonly ILogger<BarcodeStore> _logger;

        /// <summary>Initialises a new instance of the <see cref="BarcodeStore"/> class.</summary>
        /// <param name="paths">Server paths, for the fallback directory.</param>
        /// <param name="logger">The logger.</param>
        public BarcodeStore(IApplicationPaths paths, ILogger<BarcodeStore> logger)
        {
            _paths = paths;
            _logger = logger;
        }

        /// <summary>Gets the plugin's own data directory.</summary>
        public string DataRoot => Path.Combine(_paths.DataPath, "colorist");

        /// <summary>Finds an item's colour data wherever it lives.</summary>
        /// <param name="itemId">The Jellyfin item ID.</param>
        /// <param name="mediaPath">The item's media path, if it has one.</param>
        /// <returns>The path to the existing file, or null when there is none.</returns>
        public string? Locate(Guid itemId, string? mediaPath) =>
            LocateWithExtension(itemId, mediaPath, SidecarPaths.DataExtension);

        /// <summary>Finds an item's rendered image, if one was written.</summary>
        /// <param name="itemId">The Jellyfin item ID.</param>
        /// <param name="mediaPath">The item's media path, if it has one.</param>
        /// <returns>The path to the existing file, or null when there is none.</returns>
        public string? LocateImage(Guid itemId, string? mediaPath) =>
            LocateWithExtension(itemId, mediaPath, SidecarPaths.ImageExtension);

        /// <summary>Whether a barcode already exists for an item.</summary>
        /// <param name="itemId">The Jellyfin item ID.</param>
        /// <param name="mediaPath">The item's media path.</param>
        /// <returns>Whether either location holds colour data.</returns>
        /// <remarks>
        /// Keyed on the colour data alone. A leftover PNG from a version that stored
        /// only the picture does not count as done, because the colours behind it
        /// cannot be recovered from a stretched and possibly blended image — such an
        /// item genuinely does need sampling again.
        /// </remarks>
        public bool Exists(Guid itemId, string? mediaPath) => Locate(itemId, mediaPath) is not null;

        /// <summary>Writes a barcode, preferring the media folder.</summary>
        /// <param name="itemId">The Jellyfin item ID.</param>
        /// <param name="mediaPath">The item's media path.</param>
        /// <param name="data">The encoded colour data.</param>
        /// <param name="png">The rendered image, or null when images are switched off.</param>
        /// <param name="besideMedia">Whether to attempt the media folder at all.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Where the colour data was written.</returns>
        public async Task<StoredBarcode> SaveAsync(
            Guid itemId,
            string? mediaPath,
            byte[] data,
            byte[]? png,
            bool besideMedia,
            CancellationToken cancellationToken)
        {
            if (besideMedia)
            {
                var sidecar = SidecarPaths.ForMedia(mediaPath, SidecarPaths.DataExtension);
                var sidecarImage = SidecarPaths.ForMedia(mediaPath, SidecarPaths.ImageExtension);

                if (sidecar is not null && sidecarImage is not null)
                {
                    try
                    {
                        // The data write is the one allowed to send this to the
                        // fallback. Once it lands, this item lives beside the media
                        // whatever happens to the picture — a failed PNG is a missing
                        // PNG, which the detail page draws around perfectly well,
                        // whereas retrying the pair elsewhere would leave two data
                        // files disagreeing about which is current.
                        await WriteAsync(sidecar, data, cancellationToken).ConfigureAwait(false);
                        await WritePairedImageAsync(sidecarImage, png, cancellationToken).ConfigureAwait(false);

                        // Anything an earlier run left in the other location would
                        // otherwise outlive it: a library folder that was read-only
                        // when the barcode was first made and is writable now would
                        // keep serving whichever file Locate happened to find first.
                        DiscardAt(SidecarPaths.ForFallback(DataRoot, itemId, SidecarPaths.DataExtension));
                        DiscardAt(SidecarPaths.ForFallback(DataRoot, itemId, SidecarPaths.ImageExtension));

                        return new StoredBarcode(sidecar, true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Logged once per item at debug, not warning. A read-only
                        // library is a deliberate and sensible configuration, not a
                        // fault, and a warning per item would fill the log with
                        // thousands of lines describing a working setup.
                        _logger.LogDebug(
                            ex,
                            "Colorist: cannot write beside {Path}; using the plugin data directory",
                            mediaPath);
                    }
                }
            }

            var fallback = SidecarPaths.ForFallback(DataRoot, itemId, SidecarPaths.DataExtension);
            var fallbackImage = SidecarPaths.ForFallback(DataRoot, itemId, SidecarPaths.ImageExtension);

            await WriteAsync(fallback, data, cancellationToken).ConfigureAwait(false);
            await WritePairedImageAsync(fallbackImage, png, cancellationToken).ConfigureAwait(false);

            return new StoredBarcode(fallback, false);
        }

        /// <summary>Removes an item's barcode, both files, from both locations.</summary>
        /// <param name="itemId">The Jellyfin item ID.</param>
        /// <param name="mediaPath">The item's media path.</param>
        public void Delete(Guid itemId, string? mediaPath)
        {
            foreach (var extension in new[] { SidecarPaths.DataExtension, SidecarPaths.ImageExtension })
            {
                DiscardAt(SidecarPaths.ForMedia(mediaPath, extension), loud: true);
                DiscardAt(SidecarPaths.ForFallback(DataRoot, itemId, extension), loud: true);
            }
        }

        private string? LocateWithExtension(Guid itemId, string? mediaPath, string extension)
        {
            var sidecar = SidecarPaths.ForMedia(mediaPath, extension);

            if (sidecar is not null && File.Exists(sidecar))
            {
                return sidecar;
            }

            var fallback = SidecarPaths.ForFallback(DataRoot, itemId, extension);

            return File.Exists(fallback) ? fallback : null;
        }

        /// <summary>
        /// Writes the rendered image, or removes a stale one when images are off.
        /// </summary>
        /// <remarks>
        /// The removal is the part that matters. Turning images off is a request to
        /// stop putting PNGs in library folders, and leaving the ones already there
        /// would mean the setting only applies to films nobody has regenerated yet —
        /// with the leftovers frozen at whatever the settings were when they were
        /// made, which is precisely the staleness storing colour data exists to end.
        /// <para>
        /// Never throws past the caller, which is what keeps the picture from
        /// deciding where the data lives.
        /// </para>
        /// </remarks>
        private async Task WritePairedImageAsync(
            string path,
            byte[]? png,
            CancellationToken cancellationToken)
        {
            if (png is null)
            {
                DiscardAt(path);
                return;
            }

            try
            {
                await WriteAsync(path, png, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Colorist: could not write the image at {Path}", path);
            }
        }

        private void DiscardAt(string? path, bool loud = false)
        {
            if (path is null)
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (loud)
                {
                    _logger.LogWarning(ex, "Colorist: could not delete {Path}", path);
                }
                else
                {
                    _logger.LogDebug(ex, "Colorist: could not clean up {Path}", path);
                }
            }
        }

        /// <summary>
        /// Writes via a temporary file and moves it into place.
        /// </summary>
        /// <remarks>
        /// The move is what makes this safe to interrupt. Written directly, a run
        /// cancelled or crashed mid-write leaves a truncated PNG that
        /// <see cref="Exists"/> is perfectly happy with, so the item is treated as
        /// done forever and the detail page shows a broken image. A move within a
        /// directory is atomic on every filesystem that matters, so the destination
        /// only ever holds a complete file.
        /// </remarks>
        private static async Task WriteAsync(string path, byte[] png, CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = path + ".tmp";

            try
            {
                await File.WriteAllBytesAsync(temporary, png, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // The original failure is the one worth reporting.
                }

                throw;
            }
        }
    }
}
