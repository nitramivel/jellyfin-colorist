using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Colorist.Configuration;
using Jellyfin.Plugin.Colorist.Core.Runs;
using Jellyfin.Plugin.Colorist.Services.Runs;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Colorist.Services
{
    /// <summary>
    /// The background task that removes barcodes across the library.
    /// </summary>
    /// <remarks>
    /// <b>No default triggers.</b> Everything else in this plugin can be run again if
    /// it goes wrong; this cannot. It runs when somebody asks it to and never on a
    /// schedule.
    /// <para>
    /// A task rather than a loop inside the request for the same reason generation is
    /// one: a large library is tens of thousands of items, each costing up to four
    /// existence checks across storage that may be a network mount away. That is not
    /// hours of ffmpeg, but it is long enough to outlive an HTTP request, and it gives
    /// the run a progress bar and a cancel button.
    /// </para>
    /// </remarks>
    public sealed class DeleteBarcodesTask : IScheduledTask
    {
        private readonly BarcodeService _service;
        private readonly BarcodeStore _store;
        private readonly RunLogStore _runs;
        private readonly ILogger<DeleteBarcodesTask> _logger;

        /// <summary>Initialises a new instance of the <see cref="DeleteBarcodesTask"/> class.</summary>
        /// <param name="service">Used to enumerate the library.</param>
        /// <param name="store">Where barcodes are removed from.</param>
        /// <param name="runs">Where the run is recorded.</param>
        /// <param name="logger">The logger.</param>
        public DeleteBarcodesTask(
            BarcodeService service,
            BarcodeStore store,
            RunLogStore runs,
            ILogger<DeleteBarcodesTask> logger)
        {
            _service = service;
            _store = store;
            _runs = runs;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Delete Barcodes";

        /// <inheritdoc />
        public string Description =>
            "Removes every barcode Colorist has written, from the library folders and from the plugin's "
            + "data directory. Honours the \"delete only the PNGs\" setting on the Colorist page.";

        /// <inheritdoc />
        public string Category => "Colorist";

        /// <inheritdoc />
        public string Key => "ColoristDeleteBarcodes";

        /// <inheritdoc />
        /// <remarks>
        /// Empty on purpose. A scheduled delete is a way to lose work silently at
        /// three in the morning.
        /// </remarks>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

        /// <inheritdoc />
        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var imagesOnly = configuration.DeleteImagesOnly;

            // Every item the library holds, not just the ones a run would generate
            // for. Turning episodes off and then deleting must still remove the
            // episode barcodes already on disk — otherwise the setting quietly
            // decides what the delete button is allowed to reach.
            using var run = _runs.Begin(RunKind.Delete, RunTrigger.Scheduled);

            run.Configure(new RunSettings(WriteImageSidecar: !imagesOnly));

            var items = _service.GetAllItems();
            run.Plan(items.Count);

            var removed = 0;
            var completed = 0;

            _logger.LogInformation(
                "Colorist: deleting {What} across {Count} items",
                imagesOnly ? "rendered images" : "all barcodes",
                items.Count);

            try
            {
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var name = item.Name ?? "Unnamed item";
                    run.Begin(name);

                    var started = Stopwatch.GetTimestamp();
                    var count = _store.Delete(item.Id, item.Path, imagesOnly);
                    removed += count;

                    // Only items that actually held something are recorded. A delete
                    // run walks the whole library, and a log with twenty thousand
                    // lines saying "nothing here" would bury the few hundred saying
                    // what was removed.
                    if (count > 0)
                    {
                        run.Finish(new RunItem(
                            name,
                            item.Id,
                            "Removed",
                            Stopwatch.GetElapsedTime(started).TotalSeconds,
                            Path: item.Path,
                            Files: count));
                    }
                    else
                    {
                        run.Skip();
                    }

                    completed++;

                    // Reported per item rather than per file, and capped just below
                    // the end: the orphan sweep still has to run, and a bar that
                    // reads 100% while work continues is worse than one at 99%.
                    if (items.Count > 0)
                    {
                        progress.Report(completed * 99d / items.Count);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                run.Cancel();
                throw;
            }

            var orphans = _store.SweepDataDirectory(imagesOnly);

            if (orphans > 0)
            {
                run.Finish(new RunItem(
                    "Orphaned barcodes in the plugin data directory",
                    Guid.Empty,
                    "Removed",
                    0,
                    Files: orphans));
            }

            _logger.LogInformation(
                "Colorist: removed {Removed} files across the library and {Orphans} left in plugin data",
                removed.ToString(CultureInfo.InvariantCulture),
                orphans.ToString(CultureInfo.InvariantCulture));

            run.Complete();
            progress.Report(100);

            return Task.CompletedTask;
        }
    }
}
