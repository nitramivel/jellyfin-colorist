using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Colorist.Core.Runs;
using Jellyfin.Plugin.Colorist.Services.Runs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Colorist.Tests
{
    /// <summary>
    /// Run logs: what lands on disk, what the page is served, and what happens to a
    /// run whose process stopped existing.
    /// </summary>
    public sealed class RunLogStoreTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "colorist-runs-" + Guid.NewGuid().ToString("N"));

        private readonly RunLogStore _store;

        public RunLogStoreTests()
        {
            _store = new RunLogStore(new FakePaths(_root), NullLogger<RunLogStore>.Instance);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leaked temp directory is not worth failing a run over.
            }
        }

        private static RunItem Item(string name, string outcome = "Generated") =>
            new(name, Guid.NewGuid(), outcome, 1.5, Samples: 400, Columns: 400);

        [Fact]
        public void TheLiveRunIsVisibleBeforeAnythingHasFinished()
        {
            using var run = _store.Begin(RunKind.Generate, RunTrigger.Manual);
            run.Plan(10);

            var current = _store.Current();

            Assert.NotNull(current);
            Assert.Equal(RunStatus.Running, current!.Status);
            Assert.Equal(10, current.Total);
            Assert.Equal(0, current.Completed);
        }

        [Fact]
        public void CurrentIsNullWhenNothingIsRunning()
        {
            Assert.Null(_store.Current());

            using (var run = _store.Begin(RunKind.Generate, RunTrigger.Manual))
            {
                run.Plan(1);
                run.Complete();
            }

            Assert.Null(_store.Current());
        }

        [Fact]
        public void ProgressAndTotalsTrackFinishedItems()
        {
            using var run = _store.Begin(RunKind.Generate, RunTrigger.Manual);
            run.Plan(4);

            run.Finish(Item("A"));
            run.Finish(Item("B", "Skipped"));
            run.Finish(Item("C", "Failed"));

            var current = _store.Current()!;

            Assert.Equal(3, current.Completed);
            Assert.Equal(75, current.Progress);
            Assert.Equal(1, current.Totals.Generated);
            Assert.Equal(1, current.Totals.Skipped);
            Assert.Equal(1, current.Totals.Failed);
        }

        [Fact]
        public void SkippedItemsAdvanceProgressWithoutEarningALine()
        {
            // The delete run's shape: walk everything, record only what was removed.
            using var run = _store.Begin(RunKind.Delete, RunTrigger.Manual);
            run.Plan(100);

            for (var i = 0; i < 97; i++)
            {
                run.Skip();
            }

            run.Finish(new RunItem("Arrival", Guid.NewGuid(), "Removed", 0.2, Files: 2));
            run.Complete();

            var detail = _store.Detail(run.RunId)!;

            Assert.Equal(98, detail.Completed);
            Assert.Single(detail.Items);
            Assert.Equal(2, detail.Totals.FilesRemoved);
        }

        [Fact]
        public void TheCurrentItemIsReportedWhileItRuns()
        {
            using var run = _store.Begin(RunKind.Generate, RunTrigger.Manual);
            run.Plan(2);
            run.Begin("Arrival (2016)");

            Assert.Equal("Arrival (2016)", _store.Current()!.CurrentItem);

            run.Complete();
        }

        [Fact]
        public void AFinishedRunIsWrittenAndReadableBack()
        {
            Guid id;

            using (var run = _store.Begin(RunKind.Generate, RunTrigger.Scheduled))
            {
                id = run.RunId;
                run.Configure(new RunSettings("mediancut", 1000, "Auto", true, true, false, 4, false));
                run.Plan(1);
                run.Finish(Item("Arrival"));
                run.Complete();
            }

            var detail = _store.Detail(id);

            Assert.NotNull(detail);
            Assert.Equal(RunStatus.Completed, detail!.Status);
            Assert.Equal(100, detail.Progress);
            Assert.Equal(RunTrigger.Scheduled, detail.Trigger);
            Assert.Equal("mediancut", detail.Settings!.Strategy);
            Assert.NotNull(detail.FinishedAt);
            Assert.Single(detail.Items);
            Assert.Equal("Arrival", detail.Items[0].Name);
            Assert.Equal(400, detail.Items[0].Samples);
        }

        [Fact]
        public void AFailedRunKeepsItsReason()
        {
            Guid id;

            using (var run = _store.Begin(RunKind.Generate, RunTrigger.Manual))
            {
                id = run.RunId;
                run.Plan(1);
                run.Fail("ffmpeg is not on PATH");
            }

            var detail = _store.Detail(id)!;

            Assert.Equal(RunStatus.Failed, detail.Status);
            Assert.Equal("ffmpeg is not on PATH", detail.Error);
        }

        [Fact]
        public void ADroppedRunIsRecordedRatherThanLeftOpen()
        {
            // The safety net for a task that throws past its own handler: without it
            // the file stays marked running forever.
            Guid id;

            using (var run = _store.Begin(RunKind.Generate, RunTrigger.Manual))
            {
                id = run.RunId;
                run.Plan(1);
            }

            Assert.Equal(RunStatus.Failed, _store.Detail(id)!.Status);
        }

        [Fact]
        public void ARunLeftMarkedRunningReadsBackAsAbandoned()
        {
            // What a server restart mid-run leaves behind. The process that would
            // have written "abandoned" is precisely the one that stopped, so it has
            // to be worked out on read.
            var id = Guid.NewGuid();
            var document = new RunLogDocument
            {
                RunId = id,
                Kind = RunKind.Generate,
                Trigger = RunTrigger.Scheduled,
                Status = RunStatus.Running,
                StartedAt = DateTime.UtcNow.AddHours(-3),
                Total = 500,
                Completed = 120,
                CurrentItem = "Something that never finished",
            };

            Directory.CreateDirectory(_store.BasePath);
            File.WriteAllText(
                Path.Combine(_store.BasePath, id.ToString("N") + ".json"),
                System.Text.Json.JsonSerializer.Serialize(document));

            var read = _store.Detail(id)!;

            Assert.Equal(RunStatus.Abandoned, read.Status);
            Assert.Null(read.CurrentItem);
            Assert.Contains(_store.List(5), r => r.RunId == id && r.Status == RunStatus.Abandoned);
        }

        [Fact]
        public void TheNewestRunLeadsEvenWhenAnOlderOneFinishedLater()
        {
            // The bug this replaced: ordering came from the file's modification
            // time, which is when a run last wrote — effectively when it finished.
            // A long run started early stops writing after a short one started
            // late, so it sorted first despite being the older run.
            var slowStart = DateTime.UtcNow.AddHours(-3);
            var fastStart = DateTime.UtcNow.AddHours(-1);

            var slow = new RunLogDocument
            {
                RunId = Guid.NewGuid(),
                Kind = RunKind.Generate,
                Status = RunStatus.Completed,
                StartedAt = slowStart,
                FinishedAt = DateTime.UtcNow,
                Total = 1,
                Completed = 1,
            };

            var fast = new RunLogDocument
            {
                RunId = Guid.NewGuid(),
                Kind = RunKind.Generate,
                Status = RunStatus.Completed,
                StartedAt = fastStart,
                FinishedAt = fastStart.AddMinutes(2),
                Total = 1,
                Completed = 1,
            };

            Directory.CreateDirectory(_store.BasePath);

            // Written in the order they finished: the slow one last, which is what
            // gives it the newer modification time.
            Write(fast);
            System.Threading.Thread.Sleep(20);
            Write(slow);

            var listed = _store.List(5);

            Assert.Equal(fast.RunId, listed[0].RunId);
            Assert.Equal(slow.RunId, listed[1].RunId);
        }

        private void Write(RunLogDocument document)
        {
            var name = document.StartedAt.ToUniversalTime()
                    .ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture)
                + "-" + document.RunId.ToString("N") + ".json";

            File.WriteAllText(
                Path.Combine(_store.BasePath, name),
                System.Text.Json.JsonSerializer.Serialize(document));
        }

        [Fact]
        public void ARunFileFromBeforeTheNameCarriedATimeIsStillFound()
        {
            // Written by 0.3.0.0, which named files by ID alone. It has to keep
            // reading and listing, not vanish on upgrade.
            var id = Guid.NewGuid();
            var document = new RunLogDocument
            {
                RunId = id,
                Kind = RunKind.Generate,
                Status = RunStatus.Completed,
                StartedAt = DateTime.UtcNow.AddMinutes(-5),
                FinishedAt = DateTime.UtcNow,
                Total = 1,
                Completed = 1,
            };

            Directory.CreateDirectory(_store.BasePath);
            File.WriteAllText(
                Path.Combine(_store.BasePath, id.ToString("N") + ".json"),
                System.Text.Json.JsonSerializer.Serialize(document));

            Assert.NotNull(_store.Detail(id));
            Assert.Contains(_store.List(5), r => r.RunId == id);
        }

        [Fact]
        public void OlderRunFilesAreReorderedRetroactively()
        {
            // Two runs recorded by 0.3.0.0, named by ID alone. The one that started
            // FIRST finished LAST, so its modification time is the newest — which is
            // exactly the ordering that was wrong, and fixing only new runs would
            // have left this history stuck the wrong way round.
            var older = NewDocument(DateTime.UtcNow.AddHours(-4));
            var newer = NewDocument(DateTime.UtcNow.AddHours(-1));

            Directory.CreateDirectory(_store.BasePath);
            WriteLegacy(newer);
            System.Threading.Thread.Sleep(20);
            WriteLegacy(older);   // written last, so it has the newest mtime

            var listed = _store.List(5);

            Assert.Equal(newer.RunId, listed[0].RunId);
            Assert.Equal(older.RunId, listed[1].RunId);

            // And the files have been renamed, so it costs nothing next time.
            var names = Directory.GetFiles(_store.BasePath, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .ToList();

            Assert.All(names, n => Assert.Contains('-', n!));
        }

        private static RunLogDocument NewDocument(DateTime startedAt) => new()
        {
            RunId = Guid.NewGuid(),
            Kind = RunKind.Generate,
            Status = RunStatus.Completed,
            StartedAt = startedAt,
            FinishedAt = startedAt.AddMinutes(5),
            Total = 1,
            Completed = 1,
        };

        private void WriteLegacy(RunLogDocument document)
        {
            File.WriteAllText(
                Path.Combine(_store.BasePath, document.RunId.ToString("N") + ".json"),
                System.Text.Json.JsonSerializer.Serialize(document));
        }

        [Fact]
        public void ACancelledRunSaysCancelledRatherThanFailed()
        {
            Guid id;

            using (var run = _store.Begin(RunKind.Generate, RunTrigger.Manual))
            {
                id = run.RunId;
                run.Plan(10);
                run.Finish(Item("A"));
                run.Cancel();
            }

            // Disposing after Cancel must not overwrite it with the failure the
            // safety net records for a run that ended without saying anything.
            Assert.Equal(RunStatus.Cancelled, _store.Detail(id)!.Status);
            Assert.Null(_store.Detail(id)!.Error);
        }

        [Fact]
        public void TheListIsNewestFirstAndHonoursItsLimit()
        {
            for (var i = 0; i < 8; i++)
            {
                using var run = _store.Begin(RunKind.Generate, RunTrigger.Manual);
                run.Plan(1);
                run.Finish(Item("Run " + i));
                run.Complete();

                // The list orders by file write time, which has limited resolution
                // on some filesystems.
                System.Threading.Thread.Sleep(15);
            }

            var listed = _store.List(5);

            Assert.Equal(5, listed.Count);
            Assert.All(listed, r => Assert.Equal(RunStatus.Completed, r.Status));

            var times = listed.Select(r => r.StartedAt).ToList();
            Assert.Equal(times.OrderByDescending(t => t).ToList(), times);
        }

        [Fact]
        public void TheLiveRunLeadsTheListBeforeItsFileExists()
        {
            using (var earlier = _store.Begin(RunKind.Generate, RunTrigger.Manual))
            {
                earlier.Plan(1);
                earlier.Complete();
            }

            using var live = _store.Begin(RunKind.Delete, RunTrigger.Manual);
            live.Plan(50);

            var listed = _store.List(5);

            Assert.Equal(live.RunId, listed[0].RunId);
            Assert.Equal(RunStatus.Running, listed[0].Status);

            // And appears exactly once, not also as its own half-written file.
            Assert.Single(listed, r => r.RunId == live.RunId);
        }

        [Fact]
        public void OldRunsAreRotatedAway()
        {
            for (var i = 0; i < RunLogStore.RetainedRuns + 6; i++)
            {
                using var run = _store.Begin(RunKind.Generate, RunTrigger.Manual);
                run.Plan(1);
                run.Complete();
            }

            var files = Directory.GetFiles(_store.BasePath, "*.json");

            Assert.True(
                files.Length <= RunLogStore.RetainedRuns + 1,
                $"expected rotation to about {RunLogStore.RetainedRuns} files, found {files.Length}");
        }

        [Fact]
        public void AnUnknownRunIsNotFound()
        {
            Assert.Null(_store.Detail(Guid.NewGuid()));
        }

        [Fact]
        public void TheEstimateReachesTheSnapshotOnceThereIsARate()
        {
            using var run = _store.Begin(RunKind.Generate, RunTrigger.Manual);
            run.Plan(1000);

            Assert.Null(_store.Current()!.EtaSeconds);

            for (var i = 0; i < RunEstimate.Minimum + 2; i++)
            {
                run.Finish(Item("Item " + i));
                System.Threading.Thread.Sleep(12);
            }

            var current = _store.Current()!;

            Assert.NotNull(current.EtaSeconds);
            Assert.NotNull(current.ItemsPerMinute);
            Assert.True(current.EtaSeconds > 0);

            run.Complete();
        }
    }
}
