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
    /// <param name="Path">Full path to the written file.</param>
    /// <param name="BesideMedia">Whether it landed next to the media rather than in plugin data.</param>
    public readonly record struct StoredBarcode(string Path, bool BesideMedia);

    /// <summary>
    /// Reads and writes barcode files.
    /// </summary>
    /// <remarks>
    /// <b>Two locations, one lookup.</b> The preferred home is beside the media, so
    /// the image travels with the file and is visible to anything else that looks in
    /// that folder. The fallback is the plugin's data directory, for the very common
    /// case of a library mounted read-only. Everything that reads a barcode asks this
    /// class, which checks both — so nothing downstream, least of all the web client,
    /// has to know or care which one an item used.
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

        /// <summary>Finds an item's barcode wherever it lives.</summary>
        /// <param name="itemId">The Jellyfin item ID.</param>
        /// <param name="mediaPath">The item's media path, if it has one.</param>
        /// <returns>The path to the existing file, or null when there is none.</returns>
        public string? Locate(Guid itemId, string? mediaPath)
        {
            var sidecar = SidecarPaths.ForMedia(mediaPath);

            if (sidecar is not null && File.Exists(sidecar))
            {
                return sidecar;
            }

            var fallback = SidecarPaths.ForFallback(DataRoot, itemId);

            return File.Exists(fallback) ? fallback : null;
        }

        /// <summary>Whether a barcode already exists for an item.</summary>
        /// <param name="itemId">The Jellyfin item ID.</param>
        /// <param name="mediaPath">The item's media path.</param>
        /// <returns>Whether either location holds a file.</returns>
        public bool Exists(Guid itemId, string? mediaPath) => Locate(itemId, mediaPath) is not null;

        /// <summary>Writes a barcode, preferring the media folder.</summary>
        /// <param name="itemId">The Jellyfin item ID.</param>
        /// <param name="mediaPath">The item's media path.</param>
        /// <param name="png">The encoded image.</param>
        /// <param name="besideMedia">Whether to attempt the media folder at all.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Where it was written.</returns>
        public async Task<StoredBarcode> SaveAsync(
            Guid itemId,
            string? mediaPath,
            byte[] png,
            bool besideMedia,
            CancellationToken cancellationToken)
        {
            if (besideMedia)
            {
                var sidecar = SidecarPaths.ForMedia(mediaPath);

                if (sidecar is not null)
                {
                    try
                    {
                        await WriteAsync(sidecar, png, cancellationToken).ConfigureAwait(false);
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

            var fallback = SidecarPaths.ForFallback(DataRoot, itemId);
            await WriteAsync(fallback, png, cancellationToken).ConfigureAwait(false);

            return new StoredBarcode(fallback, false);
        }

        /// <summary>Removes an item's barcode from both locations.</summary>
        /// <param name="itemId">The Jellyfin item ID.</param>
        /// <param name="mediaPath">The item's media path.</param>
        public void Delete(Guid itemId, string? mediaPath)
        {
            foreach (var candidate in new[] { SidecarPaths.ForMedia(mediaPath), SidecarPaths.ForFallback(DataRoot, itemId) })
            {
                if (candidate is null)
                {
                    continue;
                }

                try
                {
                    if (File.Exists(candidate))
                    {
                        File.Delete(candidate);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Colorist: could not delete {Path}", candidate);
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
