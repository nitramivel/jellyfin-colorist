using System;
using Jellyfin.Plugin.Colorist.Core.Sampling;
using Jellyfin.Plugin.Colorist.Services;
using Xunit;

namespace Jellyfin.Plugin.Colorist.Tests
{
    public class ToneMappingPolicyTests
    {
        [Fact]
        public void PlainSdrIsLeftAlone()
        {
            // Most of any library. Tone mapping this would wash it out just as badly
            // as failing to tone map HDR.
            Assert.Equal(ToneMapping.None, ToneMappingPolicy.Decide("bt709", null, false));
            Assert.Equal(ToneMapping.None, ToneMappingPolicy.Decide(null, null, false));
            Assert.Equal(ToneMapping.None, ToneMappingPolicy.Decide("unknown", null, false));
        }

        [Theory]
        [InlineData("smpte2084")]
        [InlineData("SMPTE2084")]
        [InlineData("arib-std-b67")]
        public void PqAndHlgGetTheHdrChain(string transfer)
        {
            Assert.Equal(ToneMapping.Hdr, ToneMappingPolicy.Decide(transfer, null, false));
        }

        [Fact]
        public void DolbyVisionProfileFiveGetsLibplacebo()
        {
            // No HDR10 base layer: the pixels are IPT-PQ-C2 and decode pink and green.
            // Tone mapping alone would faithfully convert nonsense.
            Assert.Equal(ToneMapping.DolbyVision, ToneMappingPolicy.Decide("smpte2084", 5, false));
            Assert.Equal(ToneMapping.DolbyVision, ToneMappingPolicy.Decide(null, 5, false));
        }

        [Theory]
        [InlineData(7)]
        [InlineData(8)]
        public void DolbyVisionWithAnHdr10BaseIsJustHdr(int profile)
        {
            // Profiles 7 and 8.x carry a conventional base layer, so ffmpeg produces a
            // sane picture with or without understanding the RPU. No libplacebo needed.
            Assert.Equal(ToneMapping.Hdr, ToneMappingPolicy.Decide("smpte2084", profile, true));
        }

        [Fact]
        public void ProfileFiveTaggedAsBaseCompatibleIsTreatedAsOrdinaryHdr()
        {
            // Contradictory metadata. Believing the compatibility flag is the safer
            // reading: the HDR10 chain on a genuine profile 5 is wrong but recoverable,
            // whereas libplacebo on a stream with no RPU can fail outright.
            Assert.Equal(ToneMapping.Hdr, ToneMappingPolicy.Decide("smpte2084", 5, true));
        }

        [Fact]
        public void DolbyVisionWithNoStatedTransferIsStillNotTreatedAsSdr()
        {
            // Profile 5 frequently reports no transfer at all, and reading that as SDR
            // is exactly how it ends up sampled pink.
            Assert.NotEqual(ToneMapping.None, ToneMappingPolicy.Decide(null, 5, false));
            Assert.NotEqual(ToneMapping.None, ToneMappingPolicy.Decide("unknown", 8, true));
        }

        [Fact]
        public void FallbackDegradesOneStepAtATimeAndThenStops()
        {
            Assert.Equal(ToneMapping.Hdr, ToneMappingPolicy.Fallback(ToneMapping.DolbyVision));
            Assert.Equal(ToneMapping.None, ToneMappingPolicy.Fallback(ToneMapping.Hdr));
            Assert.Null(ToneMappingPolicy.Fallback(ToneMapping.None));
        }

        [Fact]
        public void FallbackTerminates()
        {
            // A ladder that could loop would retry ffmpeg forever on a broken file.
            var mode = ToneMapping.DolbyVision;

            for (var i = 0; i < 10; i++)
            {
                var next = ToneMappingPolicy.Fallback(mode);

                if (next is null)
                {
                    Assert.Equal(ToneMapping.None, mode);
                    return;
                }

                mode = next.Value;
            }

            Assert.Fail("the fallback ladder did not terminate");
        }
    }

    public class HdrProbeParsingTests
    {
        [Fact]
        public void DetectsTheRealDolbyVisionFilmFromTheLibrary()
        {
            // Trimmed from the actual ffprobe shape for a 2160p DOVIWithHDR10 title —
            // the film whose barcode came out muted and started all of this.
            const string Json = """
            {"streams":[{"codec_type":"video","width":3824,"height":2160,
              "color_transfer":"smpte2084","color_primaries":"bt2020",
              "color_space":"bt2020nc","pix_fmt":"yuv420p10le",
              "side_data_list":[{"side_data_type":"DOVI configuration record",
                "dv_profile":8,"dv_level":9,"dv_bl_signal_compatibility_id":1}]}],
             "format":{"duration":"6612.000000"}}
            """;

            var info = FrameSampler.ParseProbe(Json);

            Assert.NotNull(info);
            Assert.Equal(3824, info!.Value.Width);
            Assert.Equal(2160, info.Value.Height);

            // Profile 8.1 with a compatible base: the HDR10 chain, not libplacebo.
            Assert.Equal(ToneMapping.Hdr, info.Value.ToneMapping);
        }

        [Fact]
        public void DetectsProfileFive()
        {
            const string Json = """
            {"streams":[{"codec_type":"video","width":3840,"height":2160,
              "side_data_list":[{"side_data_type":"DOVI configuration record",
                "dv_profile":5,"dv_bl_signal_compatibility_id":0}]}],
             "format":{"duration":"600.0"}}
            """;

            var info = FrameSampler.ParseProbe(Json);

            Assert.NotNull(info);
            Assert.Equal(ToneMapping.DolbyVision, info!.Value.ToneMapping);
        }

        [Fact]
        public void OrdinarySdrFileNeedsNothing()
        {
            const string Json = """
            {"streams":[{"codec_type":"video","width":1920,"height":1080,
              "color_transfer":"bt709","color_primaries":"bt709"}],
             "format":{"duration":"5400.0"}}
            """;

            var info = FrameSampler.ParseProbe(Json);

            Assert.NotNull(info);
            Assert.Equal(ToneMapping.None, info!.Value.ToneMapping);
        }

        [Fact]
        public void MalformedSideDataDoesNotThrow()
        {
            const string Json = """
            {"streams":[{"codec_type":"video","width":1920,"height":1080,
              "side_data_list":[{"side_data_type":"Something else"},{"dv_profile":"not a number"}]}],
             "format":{"duration":"100.0"}}
            """;

            var info = FrameSampler.ParseProbe(Json);

            Assert.NotNull(info);
            Assert.Equal(ToneMapping.None, info!.Value.ToneMapping);
        }
    }

    public class ToneMappingArgumentTests
    {
        private static readonly SamplePlan Plan = new SamplePlan(30, 630, 500);

        [Fact]
        public void SdrGetsNoConversionFilters()
        {
            var args = FfmpegArguments.BuildSample("/m/f.mkv", Plan, null, true, 1, ToneMapping.None);

            Assert.DoesNotContain("zscale", args, StringComparison.Ordinal);
            Assert.DoesNotContain("libplacebo", args, StringComparison.Ordinal);
            Assert.DoesNotContain("tonemap", args, StringComparison.Ordinal);
        }

        [Fact]
        public void HdrGetsTheZscaleChainWithHighlightDesaturationOff()
        {
            var args = FfmpegArguments.BuildSample("/m/f.mkv", Plan, null, true, 1, ToneMapping.Hdr);

            Assert.Contains("zscale=t=linear", args, StringComparison.Ordinal);
            Assert.Contains("tonemap=tonemap=hable", args, StringComparison.Ordinal);
            Assert.Contains("bt709", args, StringComparison.Ordinal);

            // The filter's default highlight desaturation exists to look natural on
            // screen, which is the opposite of what a colour census wants.
            Assert.Contains("desat=0", args, StringComparison.Ordinal);
        }

        [Fact]
        public void DolbyVisionAppliesTheRpu()
        {
            var args = FfmpegArguments.BuildSample("/m/f.mkv", Plan, null, true, 1, ToneMapping.DolbyVision);

            Assert.Contains("libplacebo", args, StringComparison.Ordinal);
            Assert.Contains("apply_dolbyvision=true", args, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(ToneMapping.Hdr)]
        [InlineData(ToneMapping.DolbyVision)]
        public void ConversionRunsAfterTheRateCapAndBeforeTheDownscale(ToneMapping mode)
        {
            // After the cap so only kept frames are converted; before the downscale so
            // the averaging happens in linear BT.709 rather than in PQ, where a small
            // highlight would drag the whole frame's average up.
            var args = FfmpegArguments.BuildSample("/m/f.mkv", Plan, null, true, 1, mode);

            var fps = args.IndexOf("fps=", StringComparison.Ordinal);
            var convert = mode == ToneMapping.DolbyVision
                ? args.IndexOf("libplacebo", StringComparison.Ordinal)
                : args.IndexOf("zscale", StringComparison.Ordinal);
            var scale = args.IndexOf("scale=128", StringComparison.Ordinal);

            Assert.True(fps > 0 && convert > fps, $"fps at {fps}, conversion at {convert}");
            Assert.True(scale > convert, $"conversion at {convert}, downscale at {scale}");
        }

        [Fact]
        public void CropStillPrecedesEverything()
        {
            var args = FfmpegArguments.BuildSample(
                "/m/f.mkv", Plan, new CropRect(3824, 1600, 0, 280), true, 1, ToneMapping.Hdr);

            Assert.True(
                args.IndexOf("crop=", StringComparison.Ordinal)
                    < args.IndexOf("zscale", StringComparison.Ordinal));
        }

        [Fact]
        public void TheProbeAsksForSideDataSoDolbyVisionCanBeSeen()
        {
            // A field-list probe would omit side_data_list entirely, and every Dolby
            // Vision file would look like plain HDR.
            Assert.Contains("-show_streams", FfmpegArguments.BuildProbe("/m/f.mkv"), StringComparison.Ordinal);
        }
    }
}
