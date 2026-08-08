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
            int threads)
        {
            var builder = new StringBuilder();
            builder.Append("-hide_banner -nostdin ");

            // Before -i, so the seek is done by the demuxer rather than by decoding
            // and discarding everything up to that point.
            AppendSeek(builder, plan.StartSeconds);

            if (keyframesOnly)
            {
                // -skip_frame nokey makes the decoder throw away non-keyframes without
                // reconstructing them, which is where the order-of-magnitude saving
                // comes from: it is the inter-frame reconstruction that costs, not the
                // reading. It has to precede -i because it configures the decoder.
                //
                // The sample positions then land wherever the encoder put its
                // keyframes rather than on an even grid. For a barcode that is a fair
                // trade and arguably an improvement, since encoders place keyframes at
                // cuts — but it does mean the timestamps have to be binned onto the
                // output columns afterwards rather than assumed uniform.
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

            if (!keyframesOnly)
            {
                var fps = plan.DurationSeconds > 0
                    ? plan.Columns / plan.DurationSeconds
                    : 1;

                filters.Add(string.Create(CultureInfo.InvariantCulture, $"fps={fps:0.#####}"));
            }

            filters.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"scale={SampleWidth}:{SampleHeight}:flags=area"));

            if (keyframesOnly)
            {
                // A raw video pipe carries pixels and nothing else — no container, no
                // packet headers, no timestamps. With an even fps filter that is fine
                // because frame N's position is known by arithmetic, but keyframes
                // arrive wherever the encoder put them, and without their times there
                // is no way to place a stripe at the right point along the strip.
                // showinfo prints one line per frame to stderr, in output order, and
                // pairing the Nth line with the Nth frame off the pipe recovers what
                // rawvideo threw away. It must come last so the times reported are the
                // times of the frames actually emitted.
                filters.Add("showinfo");
            }

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
        public static string BuildProbe(string inputPath) =>
            "-hide_banner -loglevel error -print_format json -show_format "
            + "-show_entries stream=width,height,codec_type -select_streams v:0 "
            + Quote(inputPath);

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
