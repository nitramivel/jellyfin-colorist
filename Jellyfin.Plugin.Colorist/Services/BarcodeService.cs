using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Colorist.Configuration;
using Jellyfin.Plugin.Colorist.Core;
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

    /// <summary>
    /// What happened to one item, in enough detail for a run log to be worth reading.
    /// </summary>
    /// <param name="Outcome">The headline.</param>
    /// <param name="Samples">Frames actually sampled.</param>
    /// <param name="Columns">Stripes the samples were binned into.</param>
    /// <param name="Crop">The crop applied, as an ffmpeg filter string.</param>
    /// <param name="ToneMapping">The HDR conversion used, when one was.</param>
    /// <param name="Path">Where the barcode was written.</param>
    /// <param name="BesideMedia">Whether that was the library folder rather than plugin data.</param>
    /// <param name="Error">Why it failed, when it did.</param>
    /// <remarks>
    /// Everything past <paramref name="Outcome"/> is null unless the item generated,
    /// because there is nothing to report about an item that was skipped. The
    /// distinction between samples and columns is the interesting one to record: they
    /// differ when a film had fewer keyframes than the configured stripe count, which
    /// is the usual explanation for a barcode that looks coarser than expected.
    /// </remarks>
    public readonly record struct BarcodeReport(
        BarcodeOutcome Outcome,
        int? Samples = null,
        int? Columns = null,
        string? Crop = null,
        string? ToneMapping = null,
        string? Path = null,
        bool? BesideMedia = null,
        string? Error = null);

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

            return kinds.Count == 0
                ? Array.Empty<BaseItem>()
                : Query(kinds, parentId);
        }

        /// <summary>
        /// Every item Colorist could ever have written a barcode for.
        /// </summary>
        /// <returns>Every movie and episode with a file.</returns>
        /// <remarks>
        /// Ignores the movie and episode switches, which
        /// <see cref="GetEligibleItems"/> honours. Those say what a generation run
        /// should build; they must not say what a delete is allowed to reach.
        /// Somebody who turns episodes off and then deletes is asking for the episode
        /// barcodes already on disk to go, and leaving thousands of files behind
        /// because of a setting that was flipped afterwards would be a trap.
        /// </remarks>
        public IReadOnlyList<BaseItem> GetAllItems() =>
            Query([BaseItemKind.Movie, BaseItemKind.Episode], Guid.Empty);

        private IReadOnlyList<BaseItem> Query(IReadOnlyList<BaseItemKind> kinds, Guid parentId)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = [.. kinds],
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
        public async Task<BarcodeReport> GenerateAsync(
            BaseItem item,
            bool force,
            CancellationToken cancellationToken)
        {
            var configuration = Configuration;
            var mediaPath = item.Path;

            if (string.IsNullOrEmpty(mediaPath))
            {
                return new BarcodeReport(BarcodeOutcome.Ineligible);
            }

            if (!force && _store.Exists(item.Id, mediaPath))
            {
                return new BarcodeReport(BarcodeOutcome.Skipped);
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
                return new BarcodeReport(BarcodeOutcome.Failed, Error: ex.Message);
            }
        }

        private async Task<BarcodeReport> GenerateCoreAsync(
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

                return new BarcodeReport(BarcodeOutcome.Ineligible);
            }

            var crop = await ResolveCropAsync(mediaPath, plan.Value, info, configuration, cancellationToken)
                .ConfigureAwait(false);

            var strategy = StrategyFactory.Create(configuration.ColorStrategy);

            // No probe means no idea what colour space the file is in. Sampling without
            // conversion is the right default there: applying a tone map to something
            // that turns out to be SDR washes it out, and most libraries are SDR.
            var toneMapping = configuration.ToneMapHdr && info.HasValue
                ? info.Value.ToneMapping
                : ToneMapping.None;

            var samples = await _sampler.SampleAsync(
                mediaPath,
                plan.Value,
                crop,
                configuration.KeyframesOnly,
                Math.Clamp(configuration.FfmpegThreads, 0, 16),
                toneMapping,
                strategy,
                configuration.ToColorOptions(),
                cancellationToken).ConfigureAwait(false);

            if (samples.Count == 0)
            {
                return new BarcodeReport(BarcodeOutcome.Ineligible);
            }

            var columns = ColumnBinner.Bin(samples, plan.Value.Columns, plan.Value.DurationSeconds);

            if (columns.Count == 0)
            {
                return new BarcodeReport(BarcodeOutcome.Ineligible);
            }

            // What gets stored is the measurement. Stripe width, blending and height
            // are decisions the detail page makes when it draws, so changing any of
            // them costs a page reload rather than another pass over the library.
            var data = Encoding.UTF8.GetBytes(BarcodeData.Serialize(columns));

            var png = configuration.WriteImageSidecar
                ? RenderImage(columns, configuration)
                : null;

            var stored = await _store.SaveAsync(
                item.Id,
                mediaPath,
                data,
                png,
                configuration.WriteBesideMedia,
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Colorist: {Name} — {Samples} samples into {Columns} stripes, written to {Path}",
                item.Name,
                samples.Count,
                columns.Count,
                stored.Path);

            return new BarcodeReport(
                BarcodeOutcome.Generated,
                Samples: samples.Count,
                Columns: columns.Count,
                Crop: crop?.ToFilter(),
                ToneMapping: toneMapping == ToneMapping.None ? null : toneMapping.ToString(),
                Path: stored.Path,
                BesideMedia: stored.BesideMedia);
        }

        /// <summary>
        /// Renders the optional PNG beside the colour data.
        /// </summary>
        /// <remarks>
        /// Off by default. The strip on the detail page is drawn from the colour
        /// data, so nothing in the plugin reads this file — it exists for people who
        /// want a picture in the movie folder that other tools can open, and its
        /// dimensions and blending are frozen at whatever they were when it was
        /// written.
        /// </remarks>
        private static byte[] RenderImage(IReadOnlyList<Rgb> columns, PluginConfiguration configuration)
        {
            var width = Math.Clamp(configuration.OutputWidth, 64, 8000);
            var height = Math.Clamp(configuration.OutputHeight, 16, 2000);

            // The PNG follows whatever the detail page is set to draw, gradient
            // included, so the file in the folder is the strip somebody is looking at
            // rather than a second interpretation of the same data.
            var style = configuration.ResolveStyle();

            var pixels = BarcodeComposer.Compose(
                columns,
                width,
                height,
                style != BarcodeStyle.Stripes,
                style == BarcodeStyle.Gradient ? configuration.ResolveGradientBands() : 0);

            return PngWriter.Encode(pixels, width, height);
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
        /// <param name="configuration">The settings to read the budget from.</param>
        /// <returns>How many items to process at once.</returns>
        /// <remarks>
        /// The arithmetic lives in <see cref="CpuBudget"/> so it can be tested; this
        /// only supplies the processor count, which is the part that needs a machine.
        /// </remarks>
        public static int ResolveConcurrency(PluginConfiguration configuration) =>
            CpuBudget.Workers(
                configuration.MaxConcurrency,
                configuration.CpuBudgetPercent,
                Environment.ProcessorCount);
    }
}
