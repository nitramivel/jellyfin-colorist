using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Colorist.Services;
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
        private readonly ITaskManager _taskManager;
        private readonly ILogger<ColoristController> _logger;

        /// <summary>Initialises a new instance of the <see cref="ColoristController"/> class.</summary>
        /// <param name="libraryManager">Library access.</param>
        /// <param name="service">The generator.</param>
        /// <param name="store">Barcode storage.</param>
        /// <param name="taskManager">Used to queue the scheduled task.</param>
        /// <param name="logger">The logger.</param>
        public ColoristController(
            ILibraryManager libraryManager,
            BarcodeService service,
            BarcodeStore store,
            ITaskManager taskManager,
            ILogger<ColoristController> logger)
        {
            _libraryManager = libraryManager;
            _service = service;
            _store = store;
            _taskManager = taskManager;
            _logger = logger;
        }

        /// <summary>Serves an item's barcode.</summary>
        /// <param name="itemId">The item.</param>
        /// <returns>The PNG, or 404 when the item has none.</returns>
        /// <remarks>
        /// <b>Deliberately not admin-only.</b> Every viewer's browser fetches this to
        /// draw the strip on a detail page, so requiring elevation would mean the
        /// feature simply does not appear for anyone but the owner. It exposes one
        /// derived image per item and no metadata beyond what the same user can
        /// already see.
        /// <para>
        /// Bare <c>[Authorize]</c>, which applies the server's default policy —
        /// authenticated, no further requirement. There is no
        /// <c>Policies.DefaultAuthorization</c> constant on 10.11; the constants that
        /// do exist all name a specific elevated capability.
        /// </para>
        /// </remarks>
        [HttpGet("Barcode/{itemId}")]
        [Authorize]
        [Produces("image/png")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult GetBarcode([FromRoute] Guid itemId)
        {
            var item = _libraryManager.GetItemById(itemId);
            var path = _store.Locate(itemId, item?.Path);

            if (path is null)
            {
                return NotFound();
            }

            var info = new FileInfo(path);

            // Revalidated rather than blindly cached. A barcode is fetched on every
            // visit to every detail page, so answering most of those with a 304 is
            // worth the entity tag — and tagging on size and modification time means
            // regenerating an item invalidates it immediately, which matters because
            // the URL does not change when the image does.
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

            return File(stream, "image/png", info.LastWriteTimeUtc, entityTag);
        }

        /// <summary>Reports whether an item has a barcode, without transferring it.</summary>
        /// <param name="itemId">The item.</param>
        /// <returns>A small JSON object.</returns>
        /// <remarks>
        /// The client script asks this before adding anything to the page, so an item
        /// without a barcode gets no empty container and no broken image — just
        /// nothing, which is the correct appearance for a film that has not been
        /// processed yet.
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
        public ActionResult Generate()
        {
            var task = _taskManager.ScheduledTasks
                .FirstOrDefault(t => string.Equals(
                    t.ScheduledTask.Key,
                    "ColoristGenerateBarcodes",
                    StringComparison.Ordinal));

            if (task is null)
            {
                _logger.LogError("Colorist: the generation task is not registered");
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

            var outcome = await _service.GenerateAsync(item, force: true, cancellationToken)
                .ConfigureAwait(false);

            if (outcome != BarcodeOutcome.Generated)
            {
                return UnprocessableEntity(new BarcodeResult(outcome.ToString(), item.Name));
            }

            return new BarcodeResult(outcome.ToString(), item.Name);
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

    /// <summary>The result of a single-item generation.</summary>
    /// <param name="Outcome">What happened, as the outcome enum's name.</param>
    /// <param name="Name">The item's name, for display.</param>
    public sealed record BarcodeResult(string Outcome, string? Name);
}
