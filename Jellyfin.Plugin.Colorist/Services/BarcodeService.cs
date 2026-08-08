using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Colorist.Configuration;
using Jellyfin.Plugin.Colorist.Core.Color;
using Jellyfin.Plugin.Colorist.Core.Imaging;
using Jellyfin.Plugin.Colorist.Core.Sampling;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Colorist.Services
{
    /// <summary>Why an item produced no barcode.</summary>
    public enum BarcodeOutcome
    {
        /// <summary>A barcode was written.</summary>
        Generated,

        /// <summary>One already existed and regeneration was not forced.</summary>
        Skipped,

        /// <summary>The item is too short, has no file, or ffmpeg produced nothing.</summary>
        Ineligible,

        /// <summary>Something went wrong; see the log.</summary>
        Failed,
    }

    /// <summary>Generates barcodes for items.</summary>
    public sealed class BarcodeService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly FrameSampler _sampler;
        private readonly BarcodeStore _store;
        private readonly ILogger<BarcodeService> _logger;

        /// <summary>Initialises a new instance of the <see cref="BarcodeService"/> class.</summary>
        /// <param name="libraryManager">Library access.</param>
        /// <param name="sampler">The ffmpeg-driving sampler.</param>
        /// <param name="store">Where barcodes are read and written.</param>
        /// <param name="logger">The logger.</param>
        public BarcodeService(
            ILibraryManager libraryManager,
            FrameSampler sampler,
            BarcodeStore store,
            ILogger<BarcodeService> logger)
        {
            _libraryManager = libraryManager;
            _sampler = sampler;
            _store = store;
            _logger = logger;
        }

        private static PluginConfiguration Configuration =>
            Plugin.Instance?.Configuration ?? new PluginConfiguration();

        /// <summary>Finds every item a run should consider.</summary>
        /// <param name="parentId">Restrict to one library, series or season; empty for everything.</param>
        /// <returns>The eligible items.</returns>
        public IReadOnlyList<BaseItem> GetEligibleItems(Guid parentId)
        {
            var configuration = Configuration;
            var kinds = new List<BaseItemKind>(2);

            if (configuration.IncludeMovies)
            {
                kinds.Add(BaseItemKind.Movie);
            }

            if (configuration.IncludeEpisodes)
            {
                kinds.Add(BaseItemKind.Episode);
            }

            if (kinds.Count == 0)
            {
                return Array.Empty<BaseItem>();
            }

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = kinds.ToArray(),
                Recursive = true,

                // Virtual items are episodes the library knows about from metadata but
                // holds no file for — a season listing showing next week's episode.
                // They have a runtime and a name and no bytes to sample.
                IsVirtualItem = false,
            };

            if (!parentId.Equals(default))
            {
                query.ParentId = parentId;
            }

            return _libraryManager.GetItemList(query)
                .Where(static item => !string.IsNullOrEmpty(item.Path))
                .ToList();
        }

        /// <summary>Generates a barcode for one item.</summary>
        /// <param name="item">The item.</param>
        /// <param name="force">Regenerate even if one exists.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What happened.</returns>
        public async Task<BarcodeOutcome> GenerateAsync(
            BaseItem item,
            bool force,
            CancellationToken cancellationToken)
        {
            var configuration = Configuration;
            var mediaPath = item.Path;

            if (string.IsNullOrEmpty(mediaPath))
            {
                return BarcodeOutcome.Ineligible;
            }

            if (!force && _store.Exists(item.Id, mediaPath))
            {
                return BarcodeOutcome.Skipped;
            }

            try
            {
                return await GenerateCoreAsync(item, mediaPath, configuration, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Broad by intent. This runs across an entire library, and the whole
                // range of things one bad file can throw — a codec ffmpeg refuses, a
                // path that vanished mid-run, a permission that changed — must cost
                // that item and not the run.
                _logger.LogError(ex, "Colorist: failed to generate a barcode for {Name}", item.Name);
                return BarcodeOutcome.Failed;
            }
        }

        private async Task<BarcodeOutcome> GenerateCoreAsync(
            BaseItem item,
            string mediaPath,
            PluginConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var runtimeSeconds = item.RunTimeTicks.HasValue
                ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds
                : 0;

            var info = await _sampler.ProbeAsync(mediaPath, cancellationToken).ConfigureAwait(false);

            // The library's runtime is preferred over the container's because it is
            // what the rest of Jellyfin agrees the item's length is; ffprobe fills in
            // for items whose metadata never got a runtime.
            if (runtimeSeconds < SamplePlanner.MinimumRuntimeSeconds && info.HasValue)
            {
                runtimeSeconds = info.Value.DurationSeconds;
            }

            var plan = SamplePlanner.Plan(
                runtimeSeconds,
                configuration.Columns,
                configuration.HeadTrimPercent,
                configuration.TailTrimPercent);

            if (plan is null)
            {
                _logger.LogDebug(
                    "Colorist: {Name} has no usable runtime ({Seconds}s); skipping",
                    item.Name,
                    runtimeSeconds.ToString("0.#", CultureInfo.InvariantCulture));

                return BarcodeOutcome.Ineligible;
            }

            var crop = await ResolveCropAsync(mediaPath, plan.Value, info, configuration, cancellationToken)
                .ConfigureAwait(false);

            var strategy = StrategyFactory.Create(configuration.ColorStrategy);

            var samples = await _sampler.SampleAsync(
                mediaPath,
                plan.Value,
                crop,
                configuration.KeyframesOnly,
                Math.Clamp(configuration.FfmpegThreads, 0, 16),
                strategy,
                configuration.ToColorOptions(),
                cancellationToken).ConfigureAwait(false);

            if (samples.Count == 0)
            {
                return BarcodeOutcome.Ineligible;
            }

            var columns = ColumnBinner.Bin(samples, plan.Value.Columns, plan.Value.DurationSeconds);

            if (columns.Count == 0)
            {
                return BarcodeOutcome.Ineligible;
            }

            var width = Math.Clamp(configuration.OutputWidth, 64, 8000);
            var height = Math.Clamp(configuration.OutputHeight, 16, 2000);

            var pixels = BarcodeComposer.Compose(columns, width, height, configuration.Smooth);
            var png = PngWriter.Encode(pixels, width, height);

            var stored = await _store.SaveAsync(
                item.Id,
                mediaPath,
                png,
                configuration.WriteBesideMedia,
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Colorist: {Name} — {Samples} samples into {Columns} stripes, written to {Path}",
                item.Name,
                samples.Count,
                columns.Count,
                stored.Path);

            return BarcodeOutcome.Generated;
        }

        private async Task<CropRect?> ResolveCropAsync(
            string mediaPath,
            SamplePlan plan,
            VideoInfo? info,
            PluginConfiguration configuration,
            CancellationToken cancellationToken)
        {
            switch (configuration.CropMode)
            {
                case CropMode.Fixed:
                    var fixedCrop = new CropRect(
                        configuration.FixedCropWidth,
                        configuration.FixedCropHeight,
                        configuration.FixedCropX,
                        configuration.FixedCropY);

                    // Validated against this file's dimensions even though it was
                    // configured by hand. A fixed crop is applied to every item in the
                    // library, and libraries are not uniformly 1080p — the same crop
                    // that is right for a Blu-ray rip asks a 720p file for pixels it
                    // does not have, and ffmpeg fails the whole item rather than
                    // clamping.
                    if (info.HasValue && !fixedCrop.IsPlausibleFor(info.Value.Width, info.Value.Height))
                    {
                        _logger.LogWarning(
                            "Colorist: the fixed crop {Crop} does not fit {Width}x{Height} for {Path}; sampling the full frame",
                            fixedCrop.ToFilter(),
                            info.Value.Width,
                            info.Value.Height,
                            mediaPath);

                        return null;
                    }

                    return fixedCrop;

                case CropMode.Auto:
                    if (!info.HasValue)
                    {
                        return null;
                    }

                    return await _sampler.DetectCropAsync(mediaPath, plan, info.Value, cancellationToken)
                        .ConfigureAwait(false);

                default:
                    return null;
            }
        }

        /// <summary>Resolves the configured concurrency to a real number.</summary>
        /// <param name="configured">The configured value; zero means auto.</param>
        /// <returns>How many items to process at once.</returns>
        /// <remarks>
        /// A quarter of the processors, at least one. The job competing for CPU here
        /// is video transcoding for someone who is actually watching something, and
        /// leaving three quarters of the machine alone is a reasonable default for
        /// work that nobody is waiting on. Below-normal process priority does the rest.
        /// </remarks>
        public static int ResolveConcurrency(int configured) =>
            configured > 0
                ? Math.Min(configured, 32)
                : Math.Max(1, Environment.ProcessorCount / 4);
    }
}
