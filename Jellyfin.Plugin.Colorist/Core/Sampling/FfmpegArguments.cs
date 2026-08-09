using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Colorist.Core.Sampling
{
    /// <summary>
    /// Builds the ffmpeg command lines.
    /// </summary>
    /// <remarks>
    /// Argument construction is kept here, as pure string work, precisely because
    /// there is no ffmpeg on the development machine and no Jellyfin server to run
    /// against. Everything about these invocations that can be checked without
    /// executing them — filter order, escaping, that a crop precedes a scale — is
    /// checkable in a unit test, and only the execution itself is unverified.
    /// </remarks>
    public static class FfmpegArguments
    {
        /// <summary>
        /// The size each sampled frame is scaled down to before colour analysis.
        /// </summary>
        /// <remarks>
        /// Sampling at source resolution would move gigabytes per item across a pipe
        /// to compute one colour per frame. At 128×72 a frame is 27 KB, still tens of
        /// thousands of pixels — far more than enough for a histogram whose buckets
        /// are 8 levels per channel wide, and small enough that the pipe is never the
        /// bottleneck. ffmpeg's own scaler does the downsample, which averages as it
        /// goes and removes single-pixel noise for free.
        /// </remarks>
        public const int SampleWidth = 128;

        /// <summary>Height of a scaled sample frame.</summary>
        public const int SampleHeight = 72;

        /// <summary>Bytes in one scaled sample frame.</summary>
        public const int SampleFrameBytes = SampleWidth * SampleHeight * 3;

        /// <summary>Builds the cropdetect probe command.</summary>
        /// <param name="inputPath">The video file.</param>
        /// <param name="plan">The window being sampled.</param>
        /// <returns>Arguments for ffmpeg.</returns>
        /// <remarks>
        /// <b>The probe deliberately does not start at the beginning of the file.</b>
        /// Running cropdetect over the opening frames is the classic way to get this
        /// wrong: films open on black, fade up from black, or start on a title card,
        /// and cropdetect reading those proposes a crop of almost nothing. The probe
        /// starts a third of the way in, where there is reliably picture.
        /// </remarks>
        public static string BuildCropDetect(string inputPath, SamplePlan plan)
        {
            var probeStart = plan.StartSeconds + (plan.DurationSeconds / 3);
            var builder = new StringBuilder();

            builder.Append("-hide_banner -nostdin ");
            AppendSeek(builder, probeStart);
            builder.Append("-i ").Append(Quote(inputPath)).Append(' ');

            // round=2 keeps proposed dimensions even, which every codec wants and
            // which stops a one-pixel crop wobble from producing a different modal
            // value on each run. reset=0 accumulates over the whole probe rather than
            // restarting its estimate periodically.
            builder.Append("-vf ").Append(Quote("cropdetect=limit=24:round=2:reset=0")).Append(' ');
            builder.Append("-frames:v 240 -an -sn -dn -f null -");

            return builder.ToString();
        }

        /// <summary>Builds the frame-sampling command, whose stdout is raw rgb24.</summary>
        /// <param name="inputPath">The video file.</param>
        /// <param name="plan">The window and stripe count.</param>
        /// <param name="crop">Crop to apply before analysis, if any.</param>
        /// <param name="keyframesOnly">Whether to decode only keyframes.</param>
        /// <param name="threads">Decoder thread cap; 0 lets ffmpeg decide.</param>
        /// <returns>Arguments for ffmpeg.</returns>
        public static string BuildSample(
            string inputPath,
            SamplePlan plan,
            CropRect? crop,
            bool keyframesOnly,
            int threads,
            ToneMapping toneMapping = ToneMapping.None)
        {
            var builder = new StringBuilder();
            builder.Append("-hide_banner -nostdin ");

            // Before -i, so the seek is done by the demuxer rather than by decoding
            // and discarding everything up to that point.
            AppendSeek(builder, plan.StartSeconds);

            if (keyframesOnly)
            {
                // Asks the decoder to throw non-keyframes away without reconstructing
                // them, which is where the potential saving is: inter-frame
                // reconstruction is the expensive part, not reading. It must precede
                // -i because it configures the decoder.
                //
                // Treated purely as a hint. Observed on a real 2160p WEB-DL to have no
                // effect at all — every frame still arrived — so nothing downstream
                // depends on it working. When a decoder does honour it the decode gets
                // cheaper; when it does not, the fps filter below still bounds the
                // work, and the only cost is that the saving fails to materialise.
                builder.Append("-skip_frame nokey ");
            }

            if (threads > 0)
            {
                builder.Append("-threads ")
                    .Append(threads.ToString(CultureInfo.InvariantCulture))
                    .Append(' ');
            }

            builder.Append("-i ").Append(Quote(inputPath)).Append(' ');
            builder.Append("-t ")
                .Append(plan.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(' ');

            var filters = new List<string>();

            // Crop first. Scaling before cropping would blend the black bars into the
            // picture rows and there would be no way to remove them afterwards.
            if (crop.HasValue)
            {
                filters.Add(crop.Value.ToFilter());
            }

            // The rate filter is unconditional, and that is the point.
            //
            // It was previously applied only when decoding every frame, on the
            // assumption that -skip_frame nokey would thin the stream by itself in
            // keyframe mode. On a real 2160p WEB-DL it did not: a 110-minute film
            // delivered 157,791 frames for a 1,000-stripe barcode, so the decoder
            // handed over every frame and each one was scaled and colour-clustered
            // for nothing — roughly 158 times the necessary work.
            //
            // Whether that is a decoder that ignores AVDISCARD_NONKEY or something
            // about that particular encode does not actually matter, because relying
            // on the answer was the mistake. Capping the rate here bounds the work at
            // the stripe count no matter what the decoder chooses to emit.
            var fps = plan.DurationSeconds > 0
                ? plan.Columns / plan.DurationSeconds
                : 1;

            filters.Add(string.Create(CultureInfo.InvariantCulture, $"fps={fps:0.#####}"));

            switch (toneMapping)
            {
                case ToneMapping.DolbyVision:
                    filters.Add(DolbyVisionChain);
                    break;
                case ToneMapping.Hdr:
                    filters.Add(TonemapChain);
                    break;
                default:
                    break;
            }

            // Deliberately after the rate cap: scaling is per-frame work, and there is
            // no reason to resize frames that are about to be dropped.
            filters.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"scale={SampleWidth}:{SampleHeight}:flags=area"));

            builder.Append("-vf ").Append(Quote(string.Join(',', filters))).Append(' ');

            // -an -sn -dn: audio, subtitle and data streams are not merely unwanted,
            // they would be interleaved into the same stdout pipe the pixels come
            // down and there is no framing to tell them apart.
            builder.Append("-an -sn -dn -pix_fmt rgb24 -f rawvideo -");

            return builder.ToString();
        }

        /// <summary>Builds an ffprobe command reporting duration and frame size as JSON.</summary>
        /// <param name="inputPath">The video file.</param>
        /// <returns>Arguments for ffprobe.</returns>
        /// <remarks>
        /// Asks for whole streams rather than a field list because the Dolby Vision
        /// profile arrives as stream side data, and the selector syntax for reaching
        /// into a side-data list differs between ffprobe versions. The extra output is
        /// a few kilobytes of JSON parsed once per item.
        /// </remarks>
        public static string BuildProbe(string inputPath) =>
            "-hide_banner -loglevel error -print_format json -show_format "
            + "-show_streams -select_streams v:0 "
            + Quote(inputPath);

        /// <summary>
        /// Converts HDR to something the colour maths can be trusted on.
        /// </summary>
        /// <remarks>
        /// <b>Without this, every HDR item produces a washed-out barcode.</b> A PQ
        /// (<c>smpte2084</c>) signal in BT.2020 primaries decoded straight to rgb24 is
        /// reinterpreted as though it were sRGB: PQ allocates its code values across a
        /// range up to 10,000 nits, so ordinary mid-tones land far too dark, and wide
        /// BT.2020 primaries read as narrow ones pull every colour toward the neutral
        /// axis. The result is exactly the muted, dim strip observed on a 2160p
        /// Dolby Vision film.
        /// <para>
        /// The chain linearises, tone maps with Hable, then converts to BT.709.
        /// <c>desat=0</c> matters here: the filter's default desaturation of bright
        /// highlights exists to keep them looking natural on screen, which is the
        /// opposite of what a colour census wants.
        /// </para>
        /// <para>
        /// Placed after the rate cap so only the frames actually kept are converted,
        /// and before the downscale so the averaging happens in linear BT.709 rather
        /// than in PQ, where a small highlight would drag the whole average up.
        /// </para>
        /// </remarks>
        public const string TonemapChain =
            "zscale=t=linear:npl=100,format=gbrpf32le,"
            + "tonemap=tonemap=hable:desat=0,"
            + "zscale=p=bt709:t=bt709:m=bt709:r=tv";

        /// <summary>
        /// Converts Dolby Vision profile 5, which the chain above cannot handle.
        /// </summary>
        /// <remarks>
        /// <b>Profile 5 is not HDR10 with extra data on top — it is a different signal.</b>
        /// Profiles 7 and 8.1 carry a conventional HDR10 base layer, so ffmpeg decodes
        /// something meaningful whether or not it understands the Dolby Vision RPU,
        /// and <see cref="TonemapChain"/> handles them correctly. Profile 5 has no
        /// such base: its pixels are IPT-PQ-C2, and a decoder that treats them as
        /// BT.2020 YCbCr produces the notorious pink-and-green picture. Tone mapping
        /// that would faithfully convert nonsense.
        /// <para>
        /// libplacebo is the one filter in ffmpeg that reads the RPU and reconstructs
        /// the intended image, via <c>apply_dolbyvision</c>. Jellyfin's own ffmpeg
        /// builds carry it — it is what the server uses for Dolby Vision transcoding —
        /// but a distribution build may not, which is why the caller falls back.
        /// </para>
        /// </remarks>
        public const string DolbyVisionChain =
            "libplacebo=apply_dolbyvision=true:tonemapping=bt.2390:"
            + "colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv,"
            + "format=rgb24";

        private static void AppendSeek(StringBuilder builder, double seconds)
        {
            if (seconds > 0.01)
            {
                builder.Append("-ss ")
                    .Append(seconds.ToString("0.###", CultureInfo.InvariantCulture))
                    .Append(' ');
            }
        }

        /// <summary>
        /// Wraps a value in double quotes, escaping what is inside.
        /// </summary>
        /// <remarks>
        /// Media paths contain apostrophes, spaces and brackets as a matter of course
        /// — <c>Rosemary's Baby (1968)</c> breaks a naive quoting scheme on day one.
        /// Backslashes are escaped before quotes so that an escaped quote is not then
        /// re-escaped into a literal backslash followed by an unescaped quote.
        /// </remarks>
        private static string Quote(string value) =>
            "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                        .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
