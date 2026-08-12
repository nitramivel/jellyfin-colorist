using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Colorist.Core.Runs
{
    /// <summary>How a run ended, or that it has not.</summary>
    public static class RunStatus
    {
        /// <summary>The run is in progress.</summary>
        public const string Running = "running";

        /// <summary>The run finished normally.</summary>
        public const string Completed = "completed";

        /// <summary>The run was cancelled.</summary>
        public const string Cancelled = "cancelled";

        /// <summary>The run threw.</summary>
        public const string Failed = "failed";

        /// <summary>
        /// The file was found still marked running with no process behind it — the
        /// server was restarted or the plugin reloaded mid-run.
        /// </summary>
        /// <remarks>
        /// Recorded on read rather than on write, because the one thing a process
        /// that has died cannot do is update its own file to say so.
        /// </remarks>
        public const string Abandoned = "abandoned";
    }

    /// <summary>What a run was doing.</summary>
    public static class RunKind
    {
        /// <summary>Sampling colour and writing barcodes.</summary>
        public const string Generate = "generate";

        /// <summary>Removing barcodes.</summary>
        public const string Delete = "delete";
    }

    /// <summary>What started a run.</summary>
    public static class RunTrigger
    {
        /// <summary>Somebody pressed a button.</summary>
        public const string Manual = "manual";

        /// <summary>The scheduler.</summary>
        public const string Scheduled = "scheduled";
    }

    /// <summary>
    /// One item's line in the run log.
    /// </summary>
    /// <param name="Name">The item as Jellyfin names it.</param>
    /// <param name="ItemId">The item ID, so a line can be traced back.</param>
    /// <param name="Outcome">The <c>BarcodeOutcome</c> name, or "Removed" on a delete run.</param>
    /// <param name="Seconds">How long this item took.</param>
    /// <param name="Samples">Frames sampled, when it generated.</param>
    /// <param name="Columns">Stripes produced, when it generated.</param>
    /// <param name="Crop">The crop applied, or null when the whole frame was sampled.</param>
    /// <param name="ToneMapping">The HDR conversion used, or null for SDR.</param>
    /// <param name="Path">Where the barcode was written, or removed from.</param>
    /// <param name="BesideMedia">Whether that path was the library folder rather than plugin data.</param>
    /// <param name="Files">Files written or removed for this item.</param>
    /// <param name="Error">Why it failed, when it did.</param>
    /// <remarks>
    /// Everything here is what the run <i>did</i>, not what it was configured to do —
    /// the settings live once on the document. A line saying "Auto" for crop mode
    /// would repeat a setting; a line saying the crop was 1920x800+0+140 says what
    /// actually happened to that file, which is the thing worth keeping.
    /// </remarks>
    public sealed record RunItem(
        string Name,
        Guid ItemId,
        string Outcome,
        double Seconds,
        int? Samples = null,
        int? Columns = null,
        string? Crop = null,
        string? ToneMapping = null,
        string? Path = null,
        bool? BesideMedia = null,
        int? Files = null,
        string? Error = null);

    /// <summary>
    /// The tallies a run reports as it goes.
    /// </summary>
    /// <param name="Generated">Barcodes written.</param>
    /// <param name="Skipped">Items that already had one.</param>
    /// <param name="Ineligible">Too short, no file, or ffmpeg produced nothing.</param>
    /// <param name="Failed">Items that threw.</param>
    /// <param name="FilesRemoved">Files a delete run removed.</param>
    public sealed record RunTotals(
        int Generated = 0,
        int Skipped = 0,
        int Ineligible = 0,
        int Failed = 0,
        int FilesRemoved = 0);

    /// <summary>
    /// The settings a run used, recorded once so a log can be read a month later
    /// without wondering what the page said at the time.
    /// </summary>
    /// <param name="Strategy">The colour algorithm key.</param>
    /// <param name="Columns">Stripes requested per barcode.</param>
    /// <param name="CropMode">How letterboxing was handled.</param>
    /// <param name="KeyframesOnly">Whether only keyframes were decoded.</param>
    /// <param name="ToneMapHdr">Whether HDR was converted before measuring.</param>
    /// <param name="WriteImageSidecar">Whether a PNG was written alongside the colours.</param>
    /// <param name="Concurrency">Items processed at once.</param>
    /// <param name="Force">Whether existing barcodes were regenerated.</param>
    public sealed record RunSettings(
        string? Strategy = null,
        int Columns = 0,
        string? CropMode = null,
        bool KeyframesOnly = false,
        bool ToneMapHdr = false,
        bool WriteImageSidecar = false,
        int Concurrency = 0,
        bool Force = false);

    /// <summary>
    /// What the settings page shows without opening a run: everything except the
    /// per-item lines, which are the bulk of the file.
    /// </summary>
    public class RunLogSummary
    {
        /// <summary>Gets or sets the run's unique ID.</summary>
        public Guid RunId { get; set; }

        /// <summary>Gets or sets what the run was doing; see <see cref="RunKind"/>.</summary>
        public string Kind { get; set; } = RunKind.Generate;

        /// <summary>Gets or sets what started it; see <see cref="RunTrigger"/>.</summary>
        public string Trigger { get; set; } = RunTrigger.Manual;

        /// <summary>Gets or sets the status; see <see cref="RunStatus"/>.</summary>
        public string Status { get; set; } = RunStatus.Running;

        /// <summary>Gets or sets progress from 0 to 100.</summary>
        public double Progress { get; set; }

        /// <summary>Gets or sets when the run started (UTC).</summary>
        public DateTime StartedAt { get; set; }

        /// <summary>Gets or sets when it ended (UTC), or null while it runs.</summary>
        public DateTime? FinishedAt { get; set; }

        /// <summary>Gets or sets how long it took, once finished.</summary>
        public double? DurationSeconds { get; set; }

        /// <summary>Gets or sets how many items the run has to get through.</summary>
        public int Total { get; set; }

        /// <summary>Gets or sets how many it has finished.</summary>
        public int Completed { get; set; }

        /// <summary>Gets or sets the running tallies.</summary>
        public RunTotals Totals { get; set; } = new RunTotals();

        /// <summary>Gets or sets the item currently being worked on, while running.</summary>
        public string? CurrentItem { get; set; }

        /// <summary>Gets or sets the estimated seconds remaining, or null when unknown.</summary>
        public double? EtaSeconds { get; set; }

        /// <summary>Gets or sets items finished per minute, or null before there is a rate.</summary>
        public double? ItemsPerMinute { get; set; }

        /// <summary>Gets or sets the settings the run used.</summary>
        public RunSettings? Settings { get; set; }

        /// <summary>Gets or sets the failure message when the run threw.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// One run, as it sits on disk.
    /// </summary>
    /// <remarks>
    /// The summary is inherited rather than nested so the file reads as one flat
    /// object and the list endpoint can return the base type without projecting
    /// field by field.
    /// </remarks>
    public sealed class RunLogDocument : RunLogSummary
    {
        /// <summary>The document layout version, so a reader can tell old files apart.</summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>Gets or sets the schema version.</summary>
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>Gets or sets the per-item lines, in the order they finished.</summary>
        public IList<RunItem> Items { get; set; } = new List<RunItem>();
    }
}
