using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Colorist.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Colorist.Services
{
    /// <summary>
    /// The background task that builds barcodes across the library.
    /// </summary>
    /// <remarks>
    /// <b>This is the only thing that generates in bulk.</b> The API controller can
    /// queue it and can build a single item on request, but a library-wide run is
    /// potentially hours of ffmpeg and has no business inside an HTTP request: the
    /// client would time out long before the work finished, and the run would be
    /// bound to the lifetime of a connection nobody is watching.
    /// </remarks>
    public sealed class GenerateBarcodesTask : IScheduledTask
    {
        private readonly BarcodeService _service;
        private readonly ILogger<GenerateBarcodesTask> _logger;

        /// <summary>Initialises a new instance of the <see cref="GenerateBarcodesTask"/> class.</summary>
        /// <param name="service">The generator.</param>
        /// <param name="logger">The logger.</param>
        public GenerateBarcodesTask(BarcodeService service, ILogger<GenerateBarcodesTask> logger)
        {
            _service = service;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Generate Barcodes";

        /// <inheritdoc />
        public string Description =>
            "Samples colours across every movie and episode and renders each as a vertical-stripe barcode.";

        /// <inheritdoc />
        public string Category => "Colorist";

        /// <inheritdoc />
        public string Key => "ColoristGenerateBarcodes";

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [
            // Weekly rather than daily, and at an hour nobody is watching. A run with
            // nothing new to do costs one library query, but the first run on a large
            // library is an all-night job and there is no reason to risk starting it
            // twice as often as new items arrive.
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
            },
        ];

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var items = _service.GetEligibleItems(Guid.Empty);

            if (items.Count == 0)
            {
                _logger.LogInformation("Colorist: nothing to do — no eligible items");
                progress.Report(100);
                return;
            }

            var concurrency = BarcodeService.ResolveConcurrency(configuration.MaxConcurrency);

            _logger.LogInformation(
                "Colorist: {Count} items, {Concurrency} at a time",
                items.Count,
                concurrency);

            var completed = 0;
            var generated = 0;
            var skipped = 0;
            var failed = 0;

            using var gate = new SemaphoreSlim(concurrency, concurrency);
            var running = new List<Task>(items.Count);

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

                running.Add(Task.Run(
                    async () =>
                    {
                        try
                        {
                            var outcome = await _service
                                .GenerateAsync(item, configuration.ForceRegenerate, cancellationToken)
                                .ConfigureAwait(false);

                            switch (outcome)
                            {
                                case BarcodeOutcome.Generated:
                                    Interlocked.Increment(ref generated);
                                    break;
                                case BarcodeOutcome.Skipped:
                                    Interlocked.Increment(ref skipped);
                                    break;
                                case BarcodeOutcome.Failed:
                                    Interlocked.Increment(ref failed);
                                    break;
                                default:
                                    break;
                            }
                        }
                        finally
                        {
                            // Progress is reported from inside the worker, so it
                            // advances as items finish rather than as they are queued.
                            // Reporting at the point of dispatch would race to 100%
                            // immediately and then sit there for hours.
                            var done = Interlocked.Increment(ref completed);
                            progress.Report(done * 100d / items.Count);
                            gate.Release();
                        }
                    },
                    cancellationToken));
            }

            try
            {
                await Task.WhenAll(running).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation(
                    "Colorist: cancelled after {Completed} of {Total} items",
                    completed,
                    items.Count);
                throw;
            }

            _logger.LogInformation(
                "Colorist: finished — {Generated} generated, {Skipped} already had one, {Failed} failed",
                generated.ToString(CultureInfo.InvariantCulture),
                skipped.ToString(CultureInfo.InvariantCulture),
                failed.ToString(CultureInfo.InvariantCulture));

            progress.Report(100);
        }
    }
}
