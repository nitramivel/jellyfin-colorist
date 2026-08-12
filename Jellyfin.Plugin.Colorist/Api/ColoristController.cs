using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Colorist.Configuration;
using Jellyfin.Plugin.Colorist.Core;
using Jellyfin.Plugin.Colorist.Core.Runs;
using Jellyfin.Plugin.Colorist.Services;
using Jellyfin.Plugin.Colorist.Services.Runs;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Colorist.Api
{
    /// <summary>Colorist's HTTP surface.</summary>
    [ApiController]
    [Route("Colorist")]
    public class ColoristController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;
        private readonly BarcodeService _service;
        private readonly BarcodeStore _store;
        private readonly RunLogStore _runs;
        private readonly ITaskManager _taskManager;
        private readonly ILogger<ColoristController> _logger;

        /// <summary>Initialises a new instance of the <see cref="ColoristController"/> class.</summary>
        /// <param name="libraryManager">Library access.</param>
        /// <param name="service">The generator.</param>
        /// <param name="store">Barcode storage.</param>
        /// <param name="runs">Run history.</param>
        /// <param name="taskManager">Used to queue the scheduled task.</param>
        /// <param name="logger">The logger.</param>
        public ColoristController(
            ILibraryManager libraryManager,
            BarcodeService service,
            BarcodeStore store,
            RunLogStore runs,
            ITaskManager taskManager,
            ILogger<ColoristController> logger)
        {
            _libraryManager = libraryManager;
            _service = service;
            _store = store;
            _runs = runs;
            _taskManager = taskManager;
            _logger = logger;
        }

        /// <summary>What the plugin is doing right now.</summary>
        /// <returns>The live run, or that there is none.</returns>
        /// <remarks>
        /// Polled every couple of seconds by every open settings page while a run is
        /// going, which is why it is served from the run store's in-memory snapshot
        /// rather than by reading the run file back. The cost is a lock and a small
        /// allocation; the estimate is recomputed per call so it keeps moving between
        /// completions rather than freezing on a slow item.
        /// </remarks>
        [HttpGet("Status")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<ColoristStatus> GetStatus()
        {
            var current = _runs.Current();
            var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();

            return new ColoristStatus(
                current is not null,
                current,
                Environment.ProcessorCount,
                BarcodeService.ResolveConcurrency(configuration));
        }

        /// <summary>The most recent runs.</summary>
        /// <param name="limit">How many to return.</param>
        /// <returns>Their summaries, newest first.</returns>
        /// <remarks>
        /// Summaries only — the per-item lines are the bulk of a run file and a
        /// library-wide run has one per episode. Fetch those from
        /// <c>Runs/{runId}</c> when a row is actually opened.
        /// </remarks>
        [HttpGet("Runs")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IReadOnlyList<RunLogSummary>> GetRuns([FromQuery] int limit = 5) =>
            Ok(_runs.List(Math.Clamp(limit, 1, RunLogStore.RetainedRuns)));

        /// <summary>One run in full, including every item it touched.</summary>
        /// <param name="runId">The run.</param>
        /// <returns>The document, or 404.</returns>
        [HttpGet("Runs/{runId}")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<RunLogDocument> GetRun([FromRoute] Guid runId) =>
            _runs.Detail(runId) is { } document ? Ok(document) : NotFound();

        /// <summary>The running plugin version.</summary>
        /// <returns>The assembly version.</returns>
        /// <remarks>
        /// Read by the settings page purely so it can show which build is actually
        /// loaded. The point is not the number but the blank: a page served from a
        /// browser cache predating this endpoint renders nothing there, so an empty
        /// badge means you are looking at a stale page rather than a stale server —
        /// which is otherwise invisible and easy to spend an evening chasing.
        /// </remarks>
        [HttpGet("Version")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<string> GetVersion() =>
            Plugin.Instance?.Version.ToString() ?? "unknown";

        /// <summary>Serves an item's colour data.</summary>
        /// <param name="itemId">The item.</param>
        /// <returns>The colours, or 404 when the item has none.</returns>
        /// <remarks>
        /// <b>Deliberately not admin-only.</b> Every viewer's browser fetches this to
        /// draw the strip on a detail page, so requiring elevation would mean the
        /// feature simply does not appear for anyone but the owner. It exposes a few
        /// thousand colours sampled from a film the same user can already watch.
        /// <para>
        /// Bare <c>[Authorize]</c>, which applies the server's default policy —
        /// authenticated, no further requirement. There is no
        /// <c>Policies.DefaultAuthorization</c> constant on 10.11; the constants that
        /// do exist all name a specific elevated capability.
        /// </para>
        /// <para>
        /// The 404 is the existence check. Being JSON, this is fetched through
        /// <c>ApiClient</c> with a real <c>Authorization</c> header, so the client
        /// can ask for the data itself rather than asking whether to ask — one round
        /// trip per page instead of two, and no access token in a URL.
        /// </para>
        /// </remarks>
        [HttpGet("Barcode/{itemId}/Colors")]
        [Authorize]
        [Produces(BarcodeData.MediaType)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult GetBarcodeColors([FromRoute] Guid itemId)
        {
            var item = _libraryManager.GetItemById(itemId);
            var path = _store.Locate(itemId, item?.Path);

            return path is null ? NotFound() : ServeFile(path, BarcodeData.MediaType);
        }

        /// <summary>Serves an item's rendered barcode image.</summary>
        /// <param name="itemId">The item.</param>
        /// <returns>The PNG, or 404 when the item has none.</returns>
        /// <remarks>
        /// Only answers for items whose barcode was generated with image output
        /// switched on, which is not the default — the detail page draws from the
        /// colour data and never comes here. It stays because a stable URL for the
        /// picture is useful to anyone embedding a strip somewhere else.
        /// </remarks>
        [HttpGet("Barcode/{itemId}")]
        [Authorize]
        [Produces("image/png")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult GetBarcode([FromRoute] Guid itemId)
        {
            var item = _libraryManager.GetItemById(itemId);
            var path = _store.LocateImage(itemId, item?.Path);

            return path is null ? NotFound() : ServeFile(path, "image/png");
        }

        private ActionResult ServeFile(string path, string mediaType)
        {
            var info = new FileInfo(path);

            // Revalidated rather than blindly cached. A barcode is fetched on every
            // visit to every detail page, so answering most of those with a 304 is
            // worth the entity tag — and tagging on size and modification time means
            // regenerating an item invalidates it immediately, which matters because
            // the URL does not change when the colours do.
            var entityTag = new EntityTagHeaderValue(
                "\"" + info.Length.ToString("x", CultureInfo.InvariantCulture)
                     + "-" + info.LastWriteTimeUtc.Ticks.ToString("x", CultureInfo.InvariantCulture)
                     + "\"");

            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                useAsync: true);

            return File(stream, mediaType, info.LastWriteTimeUtc, entityTag);
        }

        /// <summary>Reports whether an item has a barcode, without transferring it.</summary>
        /// <param name="itemId">The item.</param>
        /// <returns>A small JSON object.</returns>
        /// <remarks>
        /// No longer on the detail page's path — the colours endpoint answers that
        /// question with its status code, in the request that also fetches the data.
        /// Kept because "has this item been processed" is the one thing a script
        /// checking on a library run wants, and a few thousand colours is a lot to
        /// transfer per item to find out.
        /// </remarks>
        [HttpGet("Barcode/{itemId}/Exists")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<BarcodeStatus> GetBarcodeStatus([FromRoute] Guid itemId)
        {
            var item = _libraryManager.GetItemById(itemId);

            return new BarcodeStatus(_store.Locate(itemId, item?.Path) is not null);
        }

        /// <summary>Queues a full generation run.</summary>
        /// <returns>202 once the task is queued.</returns>
        /// <remarks>
        /// Queues the scheduled task rather than doing the work. A library-wide run is
        /// hours of ffmpeg; running it in the request would tie it to a connection
        /// that will time out, and leave no way to watch or cancel it.
        /// </remarks>
        [HttpPost("Generate")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        public ActionResult Generate() => Queue("ColoristGenerateBarcodes");

        /// <summary>Queues removal of every barcode Colorist has written.</summary>
        /// <returns>202 once the task is queued.</returns>
        /// <remarks>
        /// Scope comes from <c>DeleteImagesOnly</c> in the configuration rather than
        /// from a parameter here, because the work is done by a scheduled task and a
        /// task takes no arguments. The settings page saves the configuration before
        /// posting this, so the checkbox next to the button is what runs.
        /// <para>
        /// Queued rather than done inline: this is bounded work, unlike generation,
        /// but a large library is still tens of thousands of existence checks against
        /// storage that may be a network mount, and the run deserves a progress bar
        /// and a cancel button rather than a connection that times out halfway.
        /// </para>
        /// </remarks>
        [HttpPost("Delete")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        public ActionResult DeleteAll() => Queue("ColoristDeleteBarcodes");

        private ActionResult Queue(string key)
        {
            var task = _taskManager.ScheduledTasks
                .FirstOrDefault(t => string.Equals(
                    t.ScheduledTask.Key,
                    key,
                    StringComparison.Ordinal));

            if (task is null)
            {
                _logger.LogError("Colorist: the {Key} task is not registered", key);
                return StatusCode(StatusCodes.Status500InternalServerError);
            }

            _taskManager.Execute(task, new TaskOptions());

            return Accepted();
        }

        /// <summary>Generates a barcode for a single item, synchronously.</summary>
        /// <param name="itemId">The item.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>200 when a barcode was produced.</returns>
        /// <remarks>
        /// The one place work happens inside a request, and it is bounded: one file,
        /// which is seconds to a couple of minutes of ffmpeg. It exists so the
        /// settings page can show what a colour algorithm actually does without
        /// committing to a library run, and it always regenerates — being asked to
        /// preview an item and getting last week's image back would defeat the point.
        /// </remarks>
        [HttpPost("Generate/{itemId}")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
        public async Task<ActionResult<BarcodeResult>> GenerateOne(
            [FromRoute] Guid itemId,
            CancellationToken cancellationToken)
        {
            var item = _libraryManager.GetItemById(itemId);

            if (item is null)
            {
                return NotFound();
            }

            var report = await _service.GenerateAsync(item, force: true, cancellationToken)
                .ConfigureAwait(false);

            var result = new BarcodeResult(report.Outcome.ToString(), item.Name);

            return report.Outcome != BarcodeOutcome.Generated
                ? UnprocessableEntity(result)
                : result;
        }

        /// <summary>Deletes an item's barcode.</summary>
        /// <param name="itemId">The item.</param>
        /// <returns>204 regardless of whether one existed.</returns>
        [HttpDelete("Barcode/{itemId}")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult DeleteBarcode([FromRoute] Guid itemId)
        {
            var item = _libraryManager.GetItemById(itemId);
            _store.Delete(itemId, item?.Path);

            return NoContent();
        }
    }

    /// <summary>Whether an item has a barcode.</summary>
    /// <param name="Exists">Whether one was found in either location.</param>
    public sealed record BarcodeStatus(bool Exists);

    /// <summary>What the plugin is doing.</summary>
    /// <param name="IsRunning">Whether a run is in progress.</param>
    /// <param name="CurrentRun">The live run, or null when idle.</param>
    /// <param name="Processors">
    /// Processors the server can see, so the settings page can say what a CPU share
    /// resolves to. This is the cgroup-limited count inside a container rather than
    /// the host's, which is the number the owner is actually budgeting.
    /// </param>
    /// <param name="Concurrency">Items the current settings would run at once.</param>
    public sealed record ColoristStatus(
        bool IsRunning,
        RunLogSummary? CurrentRun,
        int Processors,
        int Concurrency);

    /// <summary>The result of a single-item generation.</summary>
    /// <param name="Outcome">What happened, as the outcome enum's name.</param>
    /// <param name="Name">The item's name, for display.</param>
    public sealed record BarcodeResult(string Outcome, string? Name);
}
