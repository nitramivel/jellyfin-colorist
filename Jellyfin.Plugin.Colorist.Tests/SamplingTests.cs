using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Colorist.Core;
using Jellyfin.Plugin.Colorist.Core.Color;
using Jellyfin.Plugin.Colorist.Core.Sampling;
using Jellyfin.Plugin.Colorist.Services;
using Jellyfin.Plugin.Colorist.Services.Web;
using Xunit;

namespace Jellyfin.Plugin.Colorist.Tests
{
    public class SamplePlannerTests
    {
        [Fact]
        public void EpisodeAndFilmGetTheSameStripeCount()
        {
            // The property that makes barcodes comparable across items, and the reason
            // sampling is a fixed count rather than a fixed interval.
            var episode = SamplePlanner.Plan(22 * 60, 1000, 0.5, 4);
            var film = SamplePlanner.Plan(3 * 60 * 60, 1000, 0.5, 4);

            Assert.NotNull(episode);
            Assert.NotNull(film);
            Assert.Equal(1000, episode!.Value.Columns);
            Assert.Equal(1000, film!.Value.Columns);

            // Same stripes, very different seconds per stripe.
            Assert.True(film.Value.IntervalSeconds > episode.Value.IntervalSeconds * 5);
        }

        [Fact]
        public void TrimsComeOffTheEndsInProportion()
        {
            var plan = SamplePlanner.Plan(1000, 500, 1, 5);

            Assert.NotNull(plan);
            Assert.Equal(10, plan!.Value.StartSeconds, 3);
            Assert.Equal(950, plan.Value.EndSeconds, 3);
        }

        [Fact]
        public void ShortItemGetsFewerStripesRatherThanDuplicates()
        {
            // A 90-second video asked for 1,000 stripes would sample every 0.09s,
            // which is far below the rate anything on screen changes.
            var plan = SamplePlanner.Plan(90, 1000, 0, 0);

            Assert.NotNull(plan);
            Assert.True(plan!.Value.Columns < 1000, $"got {plan.Value.Columns} stripes for 90 seconds");
            Assert.True(plan.Value.Columns >= 16);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(4.9)]
        [InlineData(double.NaN)]
        public void RefusesItemsWithNoUsableRuntime(double runtime)
        {
            Assert.Null(SamplePlanner.Plan(runtime, 1000, 0.5, 4));
        }

        [Fact]
        public void AbsurdTrimsCannotProduceAnEmptyWindow()
        {
            var plan = SamplePlanner.Plan(3600, 1000, 90, 90);

            Assert.NotNull(plan);
            Assert.True(plan!.Value.DurationSeconds >= SamplePlanner.MinimumRuntimeSeconds);
            Assert.True(plan.Value.EndSeconds > plan.Value.StartSeconds);
        }

        [Fact]
        public void TrimsThatWouldSwallowAShortItemAreAbandoned()
        {
            var plan = SamplePlanner.Plan(10, 100, 40, 40);

            Assert.NotNull(plan);
            Assert.True(plan!.Value.DurationSeconds >= SamplePlanner.MinimumRuntimeSeconds);
        }
    }

    public class CropTests
    {
        [Fact]
        public void PicksTheMostFrequentlyProposedCrop()
        {
            var stderr = string.Join(
                '\n',
                "[Parsed_cropdetect_0 @ 0x1] x1:0 crop=1920:800:0:140",
                "[Parsed_cropdetect_0 @ 0x1] x1:0 crop=1920:800:0:140",
                "[Parsed_cropdetect_0 @ 0x1] x1:0 crop=1920:800:0:140",
                "[Parsed_cropdetect_0 @ 0x1] x1:0 crop=1920:1000:0:40");

            var crop = CropDetectParser.SelectModal(stderr, 1920, 1080);

            Assert.NotNull(crop);
            Assert.Equal(new CropRect(1920, 800, 0, 140), crop!.Value);
        }

        [Fact]
        public void IgnoresAbsurdProposalsEvenWhenTheyAreTheMajority()
        {
            // A long dark stretch makes cropdetect propose keeping almost nothing. It
            // must not win on frequency — this is the failure that would compute every
            // stripe from a sliver of picture.
            var stderr = string.Join(
                '\n',
                "crop=1920:200:0:440",
                "crop=1920:200:0:440",
                "crop=1920:200:0:440",
                "crop=1920:800:0:140");

            var crop = CropDetectParser.SelectModal(stderr, 1920, 1080);

            Assert.NotNull(crop);
            Assert.Equal(800, crop!.Value.Height);
        }

        [Fact]
        public void ReturnsNothingWhenTheFullFrameIsProposed()
        {
            Assert.Null(CropDetectParser.SelectModal("crop=1920:1080:0:0", 1920, 1080));
        }

        [Fact]
        public void ReturnsNothingWhenFfmpegSaidNothingUseful()
        {
            Assert.Null(CropDetectParser.SelectModal(string.Empty, 1920, 1080));
            Assert.Null(CropDetectParser.SelectModal(null, 1920, 1080));
            Assert.Null(CropDetectParser.SelectModal("Stream #0:0: Video: h264", 1920, 1080));
        }

        [Fact]
        public void TypicalScopeLetterboxingIsAccepted()
        {
            // 2.39:1 inside 16:9 removes about a quarter of the height — the case the
            // 40% guard must not reject.
            Assert.True(new CropRect(1920, 804, 0, 138).IsPlausibleFor(1920, 1080));
        }

        [Theory]
        [InlineData(1920, 200, 0, 440)]
        [InlineData(400, 1080, 760, 0)]
        [InlineData(1920, 1080, 100, 100)]
        [InlineData(0, 0, 0, 0)]
        [InlineData(-8, 100, 0, 0)]
        public void ImplausibleCropsAreRejected(int w, int h, int x, int y)
        {
            Assert.False(new CropRect(w, h, x, y).IsPlausibleFor(1920, 1080));
        }

        [Fact]
        public void FilterStringUsesInvariantFormatting()
        {
            Assert.Equal("crop=1920:800:0:140", new CropRect(1920, 800, 0, 140).ToFilter());
        }
    }

    public class FfmpegArgumentTests
    {
        private static readonly SamplePlan Plan = new SamplePlan(30, 630, 500);

        [Fact]
        public void CropIsAppliedBeforeScaling()
        {
            // Order matters and is not recoverable afterwards: scaling first blends
            // the black bars into the picture rows.
            var args = FfmpegArguments.BuildSample("/m/f.mkv", Plan, new CropRect(1920, 800, 0, 140), true, 1);

            var crop = args.IndexOf("crop=", StringComparison.Ordinal);
            var scale = args.IndexOf("scale=", StringComparison.Ordinal);

            Assert.True(crop > 0 && scale > crop, $"crop at {crop}, scale at {scale}");
        }

        [Fact]
        public void SkipFrameComesBeforeTheInput()
        {
            // -skip_frame configures the decoder, so ffmpeg ignores it after -i.
            var args = FfmpegArguments.BuildSample("/m/f.mkv", Plan, null, keyframesOnly: true, 1);

            Assert.True(
                args.IndexOf("-skip_frame", StringComparison.Ordinal)
                    < args.IndexOf("-i ", StringComparison.Ordinal));
        }

        [Fact]
        public void SeekComesBeforeTheInput()
        {
            var args = FfmpegArguments.BuildSample("/m/f.mkv", Plan, null, true, 1);

            Assert.True(
                args.IndexOf("-ss ", StringComparison.Ordinal)
                    < args.IndexOf("-i ", StringComparison.Ordinal));
        }


        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TheFrameRateIsAlwaysCapped(bool keyframesOnly)
        {
            // The regression that produced 157,791 samples for a 1,000-stripe barcode
            // on a real 2160p WEB-DL. The rate filter used to be applied only when
            // decoding every frame, trusting -skip_frame nokey to thin the stream in
            // keyframe mode — and that trust was misplaced. The cap must be present in
            // both modes so the work is bounded whatever the decoder does.
            var args = FfmpegArguments.BuildSample("/m/f.mkv", Plan, null, keyframesOnly, 1);

            Assert.Contains("fps=", args, StringComparison.Ordinal);
        }

        [Fact]
        public void TheRateCapMatchesTheRequestedStripeCount()
        {
            // 500 stripes across a 600-second window is one frame every 1.2 seconds.
            var args = FfmpegArguments.BuildSample("/m/f.mkv", Plan, null, true, 1);

            Assert.Contains("fps=0.83333", args, StringComparison.Ordinal);
        }

        [Fact]
        public void ScalingHappensAfterTheRateCap()
        {
            // Resizing frames that are about to be dropped is pure waste.
            var args = FfmpegArguments.BuildSample("/m/f.mkv", Plan, null, true, 1);

            Assert.True(
                args.IndexOf("fps=", StringComparison.Ordinal)
                    < args.IndexOf("scale=", StringComparison.Ordinal));
        }

        [Fact]
        public void NoTimestampParsingIsRequestedInEitherMode()
        {
            // Positions are arithmetic now that the rate is capped, so showinfo — which
            // emitted one stderr line per frame — is gone from both paths.
            Assert.DoesNotContain("showinfo", FfmpegArguments.BuildSample("/m/f.mkv", Plan, null, true, 1), StringComparison.Ordinal);

            var args = FfmpegArguments.BuildSample("/m/f.mkv", Plan, null, keyframesOnly: false, 1);

            Assert.Contains("fps=", args, StringComparison.Ordinal);
            Assert.DoesNotContain("showinfo", args, StringComparison.Ordinal);
            Assert.DoesNotContain("-skip_frame", args, StringComparison.Ordinal);
        }

        [Fact]
        public void AlwaysRequestsRawRgbAndNothingElse()
        {
            var args = FfmpegArguments.BuildSample("/m/f.mkv", Plan, null, true, 1);

            Assert.Contains("-pix_fmt rgb24", args, StringComparison.Ordinal);
            Assert.Contains("-f rawvideo", args, StringComparison.Ordinal);

            // Audio, subtitles and data would be interleaved into the same pipe as the
            // pixels, with no framing to tell them apart.
            Assert.Contains("-an", args, StringComparison.Ordinal);
            Assert.Contains("-sn", args, StringComparison.Ordinal);
            Assert.Contains("-dn", args, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("/media/Rosemary's Baby (1968)/film.mkv")]
        [InlineData("/media/A \"Quoted\" Title/film.mkv")]
        [InlineData("/media/Back\\slash/film.mkv")]
        public void AwkwardPathsAreQuoted(string path)
        {
            var args = FfmpegArguments.BuildSample(path, Plan, null, true, 1);

            Assert.Contains("-i \"", args, StringComparison.Ordinal);

            // Every quote in the argument string must be either a delimiter or escaped;
            // an odd number of unescaped quotes means the command line is broken.
            var unescaped = 0;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == '"' && (i == 0 || args[i - 1] != '\\'))
                {
                    unescaped++;
                }
            }

            Assert.True(unescaped % 2 == 0, $"unbalanced quoting in: {args}");
        }

        [Fact]
        public void CropDetectProbeDoesNotStartAtTheBeginningOfTheFile()
        {
            // The classic cropdetect bug: films open on black or fade up from it, and
            // probing there proposes cropping away the entire picture.
            var args = FfmpegArguments.BuildCropDetect("/m/f.mkv", new SamplePlan(0, 6000, 1000));

            Assert.Contains("-ss ", args, StringComparison.Ordinal);
            Assert.Contains("cropdetect", args, StringComparison.Ordinal);

            var seek = args.IndexOf("-ss ", StringComparison.Ordinal) + 4;
            var value = double.Parse(
                args[seek..args.IndexOf(' ', seek)],
                System.Globalization.CultureInfo.InvariantCulture);

            Assert.True(value > 100, $"probe started at {value}s, too close to the opening");
        }

        [Fact]
        public void ThreadCapIsOmittedWhenZero()
        {
            Assert.DoesNotContain("-threads", FfmpegArguments.BuildSample("/m/f.mkv", Plan, null, true, 0), StringComparison.Ordinal);
            Assert.Contains("-threads 2", FfmpegArguments.BuildSample("/m/f.mkv", Plan, null, true, 2), StringComparison.Ordinal);
        }
    }


    public class ColumnBinnerTests
    {
        private static readonly Rgb Red = new Rgb(255, 0, 0);
        private static readonly Rgb Blue = new Rgb(0, 0, 255);

        [Fact]
        public void PlacesSamplesAccordingToTheirTimestamps()
        {
            // Red for the first half, blue for the second — the strip must reflect
            // that, not the order frames happened to arrive in.
            var samples = new List<TimedSample>
            {
                new TimedSample(0, Red),
                new TimedSample(24, Red),
                new TimedSample(51, Blue),
                new TimedSample(99, Blue),
            };

            var columns = ColumnBinner.Bin(samples, 4, 100);

            Assert.Equal(4, columns.Count);
            Assert.True(columns[0].R > columns[0].B);
            Assert.True(columns[3].B > columns[3].R);
        }

        [Fact]
        public void NeverClaimsMoreStripesThanThereWereFrames()
        {
            // Asking for 1,000 stripes from 5 keyframes and filling the gaps would
            // present repetition as measurement.
            var samples = new List<TimedSample>();

            for (var i = 0; i < 5; i++)
            {
                samples.Add(new TimedSample(i * 20, Red));
            }

            Assert.Equal(5, ColumnBinner.Bin(samples, 1000, 100).Count);
        }

        [Fact]
        public void GapsHoldThePreviousColourRatherThanGoingBlack()
        {
            var samples = new List<TimedSample>
            {
                new TimedSample(0, Red),
                new TimedSample(1, Red),
                new TimedSample(2, Red),
                new TimedSample(99, Blue),
            };

            var columns = ColumnBinner.Bin(samples, 4, 100);

            foreach (var column in columns)
            {
                Assert.True(column.R > 100 || column.B > 100, $"unexpected dark stripe {column.ToHex()}");
            }
        }

        [Fact]
        public void EmptyInputProducesNoColumns()
        {
            Assert.Empty(ColumnBinner.Bin(Array.Empty<TimedSample>(), 100, 60));
        }

        [Fact]
        public void ZeroLengthWindowStillProducesAStrip()
        {
            var samples = new List<TimedSample> { new TimedSample(0, Red), new TimedSample(0, Blue) };

            Assert.Equal(2, ColumnBinner.Bin(samples, 2, 0).Count);
        }
    }

    public class SidecarPathTests
    {
        [Theory]
        [InlineData("-thumb")]
        [InlineData("-poster")]
        [InlineData("-fanart")]
        [InlineData("-banner")]
        [InlineData("-logo")]
        [InlineData("-clearart")]
        [InlineData("-disc")]
        [InlineData("-backdrop")]
        [InlineData("-landscape")]
        public void SuffixIsNotOneJellyfinClaimsAsArtwork(string reserved)
        {
            // If this ever fails, every barcode becomes the item's poster on the next
            // library scan.
            Assert.NotEqual(SidecarPaths.Suffix, reserved);
        }

        [Fact]
        public void SitsBesideTheVideoFile()
        {
            var path = SidecarPaths.ForMedia(Path.Combine("/media", "Arrival (2016)", "Arrival (2016).mkv"));

            Assert.Equal(
                Path.Combine("/media", "Arrival (2016)", "Arrival (2016)-colorist.png"),
                path);
        }

        [Fact]
        public void EpisodesLandInTheSeasonFolderAndDoNotCollide()
        {
            var first = SidecarPaths.ForMedia("/tv/Show/Season 01/Show - S01E01.mkv");
            var second = SidecarPaths.ForMedia("/tv/Show/Season 01/Show - S01E02.mkv");

            Assert.Equal(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
            Assert.NotEqual(first, second);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void RefusesAMissingPath(string? path)
        {
            Assert.Null(SidecarPaths.ForMedia(path));
        }

        [Fact]
        public void FallbackIsShardedAndKeyedOnTheItemId()
        {
            var id = Guid.Parse("1dd662e3-27c3-4e43-bbfe-108509a0b84f");
            var path = SidecarPaths.ForFallback("/data/colorist", id);

            Assert.Contains("1dd662e327c34e43bbfe108509a0b84f", path, StringComparison.Ordinal);
            Assert.Contains(Path.Combine("barcodes", "1d"), path, StringComparison.Ordinal);
        }

        [Fact]
        public void RecognisesItsOwnFiles()
        {
            Assert.True(SidecarPaths.IsBarcodeFile("/m/Arrival-colorist.png"));
            Assert.False(SidecarPaths.IsBarcodeFile("/m/Arrival-thumb.jpg"));
            Assert.False(SidecarPaths.IsBarcodeFile("/m/Arrival.mkv"));
            Assert.False(SidecarPaths.IsBarcodeFile(null));
        }
    }

    public class ProbeParsingTests
    {
        [Fact]
        public void ReadsDimensionsAndDuration()
        {
            const string Json = """
                {"streams":[{"width":1920,"height":1080,"codec_type":"video"}],
                 "format":{"duration":"7241.234000"}}
                """;

            var info = FrameSampler.ParseProbe(Json);

            Assert.NotNull(info);
            Assert.Equal(1920, info!.Value.Width);
            Assert.Equal(1080, info.Value.Height);
            Assert.Equal(7241.234, info.Value.DurationSeconds, 3);
        }

        [Fact]
        public void SurvivesAMissingDuration()
        {
            var info = FrameSampler.ParseProbe("""{"streams":[{"width":720,"height":576}]}""");

            Assert.NotNull(info);
            Assert.Equal(0, info!.Value.DurationSeconds);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json at all")]
        [InlineData("""{"streams":[]}""")]
        [InlineData("""{"streams":[{"codec_type":"audio"}]}""")]
        public void RefusesOutputWithNoUsableVideoStream(string json)
        {
            Assert.Null(FrameSampler.ParseProbe(json));
        }
    }

    public class ScriptInjectorTests
    {
        [Fact]
        public void InsertsExactlyOneTagBeforeTheClosingBody()
        {
            var patched = ScriptInjector.Patch("<html><body><div>x</div></body></html>");

            Assert.NotNull(patched);
            Assert.Contains("colorist-client", patched!, StringComparison.Ordinal);
            Assert.EndsWith("</body></html>", patched, StringComparison.Ordinal);
        }

        [Fact]
        public void RefusesToStackTagsOnADocumentItAlreadyPatched()
        {
            var once = ScriptInjector.Patch("<html><body></body></html>");

            Assert.NotNull(once);
            Assert.Null(ScriptInjector.Patch(once!));
        }

        [Theory]
        [InlineData("")]
        [InlineData("not html")]
        [InlineData("<html><head></head></html>")]
        public void LeavesDocumentsWithNoBodyAlone(string body)
        {
            // Returning null means "serve the original untouched" — the safe answer
            // for anything that is not the client shell.
            Assert.Null(ScriptInjector.Patch(body));
        }

        [Fact]
        public void TheClientScriptIsActuallyEmbedded()
        {
            // Found by resource-name suffix at runtime, so a rename of the folder or
            // the root namespace would break it silently — the symptom being a detail
            // page that simply never shows a barcode.
            Assert.NotEqual(0, ScriptInjector.ReadScript().Length);
        }

        [Fact]
        public void TheDisplayHeightSettingCanReachTheScript()
        {
            // The height is substituted into the served script by regex. If the
            // declaration in colorist.js is ever reworded — const instead of var, a
            // different name, extra spacing — the substitution silently stops
            // matching and the setting quietly does nothing. This pins the shape.
            var script = ScriptInjector.ReadScript();

            Assert.Matches(@"var DISPLAY_HEIGHT = \d+;", script);
        }

        [Fact]
        public void TheStripIsAnchoredToThePageRootNotTheIndentedContent()
        {
            // .detailPageContent carries padding-left: 32.45vw on wide layouts, so
            // anchoring there would indent the strip a third of the way across the
            // screen instead of spanning the page.
            var script = ScriptInjector.ReadScript();

            var pageRoot = script.IndexOf(".itemDetailPage:not(.hide)", StringComparison.Ordinal);
            var content = script.IndexOf("querySelector('.detailPageContent')", StringComparison.Ordinal);

            Assert.True(pageRoot > 0, "the page root selector is missing");
            Assert.True(
                content < 0 || pageRoot < content,
                "the page root must be preferred over the indented content container");
        }
    }
}
