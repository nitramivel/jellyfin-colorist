using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jellyfin.Plugin.Colorist.Core.Runs;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Colorist.Services.Runs
{
    /// <summary>
    /// One run, being written to.
    /// </summary>
    /// <remarks>
    /// Handed to a task so it can record what it did without knowing where run logs
    /// live or when they are flushed. Every member is safe to call from the several
    /// workers a generation run has going at once.
    /// </remarks>
    public interface IRunLog : IDisposable
    {
        /// <summary>Gets the run's ID.</summary>
        Guid RunId { get; }

        /// <summary>Records the settings this run is using.</summary>
        /// <param name="settings">The settings.</param>
        void Configure(RunSettings settings);

        /// <summary>Declares how many items the run will get through.</summary>
        /// <param name="total">The item count.</param>
        void Plan(int total);

        /// <summary>Names the item now being worked on.</summary>
        /// <param name="name">The item's name.</param>
        void Begin(string name);

        /// <summary>Records a finished item.</summary>
        /// <param name="item">What happened to it.</param>
        void Finish(RunItem item);

        /// <summary>
        /// Counts an item as done without giving it a line.
        /// </summary>
        /// <remarks>
        /// For work that had nothing to do. A delete run walks every item in the
        /// library and removes files for a few hundred of them; recording the other
        /// nineteen thousand as "nothing here" would bury the answer. Progress and
        /// the estimate still need them, because they are items the run got through.
        /// </remarks>
        void Skip();

        /// <summary>Marks the run finished normally.</summary>
        void Complete();

        /// <summary>Marks the run cancelled.</summary>
        void Cancel();

        /// <summary>Marks the run failed.</summary>
        /// <param name="error">Why.</param>
        void Fail(string error);
    }

    /// <summary>
    /// Where run logs are written, kept and read back.
    /// </summary>
    /// <remarks>
    /// <b>Memory answers the page; the file answers history.</b> A run in progress is
    /// polled every couple of seconds by every open settings page, and serving that
    /// from disk would mean re-reading and re-parsing a file that grows with every
    /// item. The live snapshot is held in memory and costs a lock; the file exists so
    /// the run survives a restart and can be read a month later.
    /// <para>
    /// That split is also why writes are debounced. Nothing is waiting on the file
    /// while the run is going, so writing it on every completed item would be pure
    /// I/O for no reader — on a large library, tens of thousands of rewrites of a
    /// document that only grows.
    /// </para>
    /// </remarks>
    public sealed class RunLogStore
    {
        /// <summary>
        /// How many run files to keep.
        /// </summary>
        /// <remarks>
        /// The page shows five. Twenty is kept so that looking further back is
        /// possible without the directory growing without limit — a full library run
        /// records one line per episode, which on a large library is a file of a few
        /// hundred kilobytes.
        /// </remarks>
        public const int RetainedRuns = 20;

        /// <summary>How long a run may go unwritten while it is in progress.</summary>
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,

            // Film titles are full of apostrophes, colons and accented letters. The
            // default encoder turns those into \u sequences, which makes a run log
            // something nobody can read in a text editor.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly IApplicationPaths _paths;
        private readonly ILogger<RunLogStore> _logger;
        private readonly object _gate = new();

        private RunWriter? _current;

        /// <summary>Initialises a new instance of the <see cref="RunLogStore"/> class.</summary>
        /// <param name="paths">Server paths, for the data directory.</param>
        /// <param name="logger">The logger.</param>
        public RunLogStore(IApplicationPaths paths, ILogger<RunLogStore> logger)
        {
            _paths = paths;
            _logger = logger;
        }

        /// <summary>Gets the directory run logs live in.</summary>
        public string BasePath => Path.Combine(_paths.DataPath, "colorist", "runs");

        /// <summary>Starts a run.</summary>
        /// <param name="kind">What it is doing; see <see cref="RunKind"/>.</param>
        /// <param name="trigger">What started it; see <see cref="RunTrigger"/>.</param>
        /// <returns>The log to record into.</returns>
        public IRunLog Begin(string kind, string trigger)
        {
            var writer = new RunWriter(this, kind, trigger);

            lock (_gate)
            {
                _current = writer;
            }

            Prune();

            return writer;
        }

        /// <summary>The run in progress, if there is one.</summary>
        /// <returns>A snapshot, or null when idle.</returns>
        public RunLogSummary? Current()
        {
            RunWriter? writer;

            lock (_gate)
            {
                writer = _current;
            }

            return writer?.Snapshot();
        }

        /// <summary>The most recent runs, newest first.</summary>
        /// <param name="limit">How many to return.</param>
        /// <returns>Their summaries.</returns>
        /// <remarks>
        /// The live run is served from memory and the rest from disk, so a run that
        /// has just started appears in the list before its file has been written.
        /// </remarks>
        public IReadOnlyList<RunLogSummary> List(int limit = 5)
        {
            var live = Current();
            var results = new List<RunLogSummary>(limit);

            if (live is not null)
            {
                results.Add(live);
            }

            foreach (var file in EnumerateRunFiles())
            {
                if (results.Count >= limit)
                {
                    break;
                }

                var document = Read(file);

                if (document is null || (live is not null && document.RunId == live.RunId))
                {
                    continue;
                }

                results.Add(Reconcile(document));
            }

            return results;
        }

        /// <summary>One run in full, including its per-item lines.</summary>
        /// <param name="runId">The run.</param>
        /// <returns>The document, or null when there is no such run.</returns>
        public RunLogDocument? Detail(Guid runId)
        {
            lock (_gate)
            {
                if (_current is not null && _current.RunId == runId)
                {
                    return _current.Document();
                }
            }

            var document = Read(PathFor(runId));

            if (document is null)
            {
                return null;
            }

            Reconcile(document);

            return document;
        }

        /// <summary>
        /// Corrects a document that says it is still running when it plainly is not.
        /// </summary>
        /// <remarks>
        /// A run whose file says "running" and which is not the live one belongs to a
        /// process that no longer exists — the server was restarted, or the plugin
        /// reloaded, mid-run. Nothing can have written "abandoned" into that file at
        /// the time, because the thing that would have written it is what stopped. So
        /// it is worked out on read.
        /// </remarks>
        private static RunLogSummary Reconcile(RunLogDocument document)
        {
            if (document.Status == RunStatus.Running)
            {
                document.Status = RunStatus.Abandoned;
                document.EtaSeconds = null;
                document.CurrentItem = null;
            }

            return document;
        }

        private string PathFor(Guid runId) =>
            Path.Combine(BasePath, runId.ToString("N", CultureInfo.InvariantCulture) + ".json");

        private IEnumerable<string> EnumerateRunFiles()
        {
            if (!Directory.Exists(BasePath))
            {
                return Array.Empty<string>();
            }

            try
            {
                // Ordered by name, which sorts by the timestamp prefix... except it
                // does not: files are named by run ID. Ordered by write time instead,
                // which is what "most recent" actually means here.
                return new DirectoryInfo(BasePath)
                    .GetFiles("*.json")
                    .OrderByDescending(static f => f.LastWriteTimeUtc)
                    .Select(static f => f.FullName)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Colorist: could not list run logs in {Path}", BasePath);
                return Array.Empty<string>();
            }
        }

        private RunLogDocument? Read(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                using var stream = File.OpenRead(path);
                return JsonSerializer.Deserialize<RunLogDocument>(stream, SerializerOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A half-written file from a run that was killed mid-flush reads as
                // absent rather than as an error, because there is nothing the reader
                // can do about it and one bad file must not take the list down.
                _logger.LogDebug(ex, "Colorist: could not read the run log at {Path}", path);
                return null;
            }
        }

        private void Prune()
        {
            try
            {
                foreach (var file in EnumerateRunFiles().Skip(RetainedRuns))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Colorist: could not prune old run logs");
            }
        }

        private void Write(RunLogDocument document)
        {
            try
            {
                Directory.CreateDirectory(BasePath);

                var path = PathFor(document.RunId);
                var temporary = path + ".tmp";

                File.WriteAllText(temporary, JsonSerializer.Serialize(document, SerializerOptions));
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Colorist: could not write the run log for {RunId}", document.RunId);
            }
        }

        private void Retire(RunWriter writer)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, writer))
                {
                    _current = null;
                }
            }
        }

        /// <summary>
        /// The live run: a document, a clock and a lock.
        /// </summary>
        private sealed class RunWriter : IRunLog
        {
            private readonly RunLogStore _store;
            private readonly RunLogDocument _document;
            private readonly List<DateTime> _completions = new();
            private readonly Stopwatch _sinceFlush = Stopwatch.StartNew();
            private readonly object _lock = new();

            private bool _finished;

            public RunWriter(RunLogStore store, string kind, string trigger)
            {
                _store = store;
                _document = new RunLogDocument
                {
                    RunId = Guid.NewGuid(),
                    Kind = kind,
                    Trigger = trigger,
                    Status = RunStatus.Running,
                    StartedAt = DateTime.UtcNow,
                };
            }

            public Guid RunId => _document.RunId;

            public void Configure(RunSettings settings)
            {
                lock (_lock)
                {
                    _document.Settings = settings;
                    Persist(force: true);
                }
            }

            public void Plan(int total)
            {
                lock (_lock)
                {
                    _document.Total = total;
                    Persist(force: true);
                }
            }

            public void Begin(string name)
            {
                lock (_lock)
                {
                    // Not persisted. This changes several times a second on a fast
                    // run and is only ever read from the live snapshot; writing the
                    // file for it would be the whole reason the debounce exists.
                    _document.CurrentItem = name;
                }
            }

            public void Skip()
            {
                lock (_lock)
                {
                    Advance();
                    Persist(force: false);
                }
            }

            public void Finish(RunItem item)
            {
                lock (_lock)
                {
                    _document.Items.Add(item);
                    Advance();

                    var totals = _document.Totals;
                    _document.Totals = item.Outcome switch
                    {
                        nameof(BarcodeOutcome.Generated) => totals with { Generated = totals.Generated + 1 },
                        nameof(BarcodeOutcome.Skipped) => totals with { Skipped = totals.Skipped + 1 },
                        nameof(BarcodeOutcome.Ineligible) => totals with { Ineligible = totals.Ineligible + 1 },
                        nameof(BarcodeOutcome.Failed) => totals with { Failed = totals.Failed + 1 },
                        _ => totals with { FilesRemoved = totals.FilesRemoved + (item.Files ?? 0) },
                    };

                    Persist(force: false);
                }
            }

            /// <summary>
            /// Counts one item as got through. Caller holds the lock.
            /// </summary>
            /// <remarks>
            /// The completion timestamp is what the estimate is built from, so it is
            /// recorded for skipped items too — they take time, and on a delete run
            /// they are nearly all of it.
            /// </remarks>
            private void Advance()
            {
                _completions.Add(DateTime.UtcNow);
                _document.Completed = _completions.Count;

                if (_document.Total > 0)
                {
                    _document.Progress = Math.Min(100d, _document.Completed * 100d / _document.Total);
                }
            }

            public void Complete() => Finish(RunStatus.Completed, null);

            public void Cancel() => Finish(RunStatus.Cancelled, null);

            public void Fail(string error) => Finish(RunStatus.Failed, error);

            /// <summary>
            /// Ends the run if nothing else has.
            /// </summary>
            /// <remarks>
            /// The safety net for a task that throws something the caller did not
            /// catch: without it the file would stay marked running and be read back
            /// as abandoned, which is a worse description of a crash than "failed"
            /// but a better one than a run that silently never ended.
            /// </remarks>
            public void Dispose()
            {
                lock (_lock)
                {
                    if (!_finished)
                    {
                        Finish(RunStatus.Failed, "The run ended without reporting an outcome.");
                    }
                }
            }

            /// <summary>The live view, with the estimate computed fresh.</summary>
            public RunLogSummary? Snapshot()
            {
                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    var eta = RunEstimate.Remaining(_completions, _document.Total, now);
                    var rate = RunEstimate.ItemsPerSecond(_completions, now);

                    return new RunLogSummary
                    {
                        RunId = _document.RunId,
                        Kind = _document.Kind,
                        Trigger = _document.Trigger,
                        Status = _document.Status,
                        Progress = _document.Progress,
                        StartedAt = _document.StartedAt,
                        FinishedAt = _document.FinishedAt,
                        DurationSeconds = _document.DurationSeconds
                            ?? (now - _document.StartedAt).TotalSeconds,
                        Total = _document.Total,
                        Completed = _document.Completed,
                        Totals = _document.Totals,
                        CurrentItem = _document.CurrentItem,
                        EtaSeconds = eta?.TotalSeconds,
                        ItemsPerMinute = rate is null ? null : rate.Value * 60,
                        Settings = _document.Settings,
                        Error = _document.Error,
                    };
                }
            }

            /// <summary>A copy deep enough for a reader to hold while the run continues.</summary>
            public RunLogDocument Document()
            {
                lock (_lock)
                {
                    return new RunLogDocument
                    {
                        SchemaVersion = _document.SchemaVersion,
                        RunId = _document.RunId,
                        Kind = _document.Kind,
                        Trigger = _document.Trigger,
                        Status = _document.Status,
                        Progress = _document.Progress,
                        StartedAt = _document.StartedAt,
                        FinishedAt = _document.FinishedAt,
                        DurationSeconds = _document.DurationSeconds,
                        Total = _document.Total,
                        Completed = _document.Completed,
                        Totals = _document.Totals,
                        CurrentItem = _document.CurrentItem,
                        Settings = _document.Settings,
                        Error = _document.Error,

                        // The records are immutable, so copying the list is enough.
                        Items = _document.Items.ToList(),
                    };
                }
            }

            private void Finish(string status, string? error)
            {
                if (_finished)
                {
                    return;
                }

                _finished = true;

                var finishedAt = DateTime.UtcNow;
                _document.Status = status;
                _document.Error = error;
                _document.FinishedAt = finishedAt;
                _document.DurationSeconds = (finishedAt - _document.StartedAt).TotalSeconds;
                _document.CurrentItem = null;
                _document.EtaSeconds = null;

                if (status == RunStatus.Completed)
                {
                    _document.Progress = 100;
                }

                Persist(force: true);
                _store.Retire(this);
            }

            private void Persist(bool force)
            {
                if (!force && _sinceFlush.Elapsed < FlushInterval)
                {
                    return;
                }

                _sinceFlush.Restart();
                _store.Write(_document);
            }
        }
    }
}
