using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Colorist.Core.Color;
using Jellyfin.Plugin.Colorist.Core.Sampling;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Colorist.Services
{
    /// <summary>What ffprobe could tell us about a file.</summary>
    /// <param name="Width">Coded width of the first video stream.</param>
    /// <param name="Height">Coded height of the first video stream.</param>
    /// <param name="DurationSeconds">Container duration, or zero if absent.</param>
    /// <param name="ToneMapping">What conversion the colours need before sampling.</param>
    public readonly record struct VideoInfo(
        int Width,
        int Height,
        double DurationSeconds,
        ToneMapping ToneMapping);

    /// <summary>
    /// Drives ffmpeg and turns its output into one colour per sampled frame.
    /// </summary>
    public sealed class FrameSampler
    {
        private readonly IMediaEncoder _mediaEncoder;
        private readonly FfmpegRunner _runner;
        private readonly ILogger<FrameSampler> _logger;

        /// <summary>Initialises a new instance of the <see cref="FrameSampler"/> class.</summary>
        /// <param name="mediaEncoder">Supplies the ffmpeg and ffprobe paths.</param>
        /// <param name="runner">Process runner.</param>
        /// <param name="logger">The logger.</param>
        public FrameSampler(IMediaEncoder mediaEncoder, FfmpegRunner runner, ILogger<FrameSampler> logger)
        {
            _mediaEncoder = mediaEncoder;
            _runner = runner;
            _logger = logger;
        }

        /// <summary>
        /// Gets the ffmpeg binary Jellyfin is configured to use.
        /// </summary>
        /// <remarks>
        /// Taken from the server rather than assumed to be on PATH. In a container
        /// there is frequently no ffmpeg on PATH at all, and where there is one it is
        /// often a different build from the one the server was configured with —
        /// which is exactly the kind of difference that produces a plugin working on
        /// one install and silently failing on another.
        /// </remarks>
        public string? EncoderPath => Nullable(_mediaEncoder.EncoderPath);

        /// <summary>Gets the ffprobe binary Jellyfin is configured to use.</summary>
        public string? ProbePath => Nullable(_mediaEncoder.ProbePath);

        /// <summary>Reads dimensions and duration.</summary>
        /// <param name="mediaPath">The video file.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What was found, or null if ffprobe could not be run or understood.</returns>
        public async Task<VideoInfo?> ProbeAsync(string mediaPath, CancellationToken cancellationToken)
        {
            var probe = ProbePath;

            if (probe is null)
            {
                return null;
            }

            // ffprobe reports on stdout and keeps stderr for diagnostics, which is the
            // opposite way round from the cropdetect invocation below.
            var json = string.Empty;

            var result = await _runner.RunAsync(
                probe,
                FfmpegArguments.BuildProbe(mediaPath),
                async (stdout, token) =>
                {
                    using var reader = new StreamReader(stdout);
                    json = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                _logger.LogWarning(
                    "Colorist: ffprobe exited {Code} for {Path}: {Error}",
                    result.ExitCode,
                    mediaPath,
                    result.StandardError);
                return null;
            }

            return ParseProbe(json);
        }

        /// <summary>Parses ffprobe's JSON. Separated out so it can be tested without ffprobe.</summary>
        /// <param name="json">ffprobe's stdout.</param>
        /// <returns>The parsed info, or null when the JSON holds no usable video stream.</returns>
        internal static VideoInfo? ParseProbe(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                var width = 0;
                var height = 0;
                string? transferName = null;
                int? dolbyVisionProfile = null;
                var hasHdr10Base = false;

                if (root.TryGetProperty("streams", out var streams)
                    && streams.ValueKind == JsonValueKind.Array)
                {
                    foreach (var stream in streams.EnumerateArray())
                    {
                        if (stream.TryGetProperty("width", out var w) && w.TryGetInt32(out var wv))
                        {
                            width = wv;
                        }

                        if (stream.TryGetProperty("height", out var h) && h.TryGetInt32(out var hv))
                        {
                            height = hv;
                        }

                        if (stream.TryGetProperty("color_transfer", out var transfer)
                            && transfer.ValueKind == JsonValueKind.String)
                        {
                            transferName = transfer.GetString();
                        }

                        ReadDolbyVision(stream, ref dolbyVisionProfile, ref hasHdr10Base);

                        if (width > 0 && height > 0)
                        {
                            break;
                        }
                    }
                }

                double duration = 0;

                if (root.TryGetProperty("format", out var format)
                    && format.TryGetProperty("duration", out var durationElement)
                    && durationElement.ValueKind == JsonValueKind.String
                    && double.TryParse(
                        durationElement.GetString(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    duration = parsed;
                }

                if (width <= 0 || height <= 0)
                {
                    return null;
                }

                return new VideoInfo(
                    width,
                    height,
                    duration,
                    ToneMappingPolicy.Decide(transferName, dolbyVisionProfile, hasHdr10Base));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Pulls the Dolby Vision configuration out of a stream's side data.
        /// </summary>
        /// <remarks>
        /// <c>dv_bl_signal_compatibility_id</c> is the field that decides whether the
        /// base layer stands on its own. A value of 1 means the base is ordinary
        /// HDR10 and any decoder produces a sane picture from it; 2 means SDR-
        /// compatible; 4 means HLG. Zero — or the field being absent on a profile 5
        /// stream — means there is no compatible base, and the RPU has to be applied
        /// before the pixels mean anything.
        /// </remarks>
        private static void ReadDolbyVision(
            JsonElement stream,
            ref int? profile,
            ref bool hasHdr10Base)
        {
            if (!stream.TryGetProperty("side_data_list", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var entry in list.EnumerateArray())
            {
                // The ValueKind check is not redundant. TryGetInt32 throws
                // InvalidOperationException when the element is not a number rather
                // than returning false, so a container holding a string where a
                // profile number belongs would take down the probe — and with it the
                // item — instead of merely being ignored.
                if (!entry.TryGetProperty("dv_profile", out var dv)
                    || dv.ValueKind != JsonValueKind.Number
                    || !dv.TryGetInt32(out var value))
                {
                    continue;
                }

                profile = value;

                if (entry.TryGetProperty("dv_bl_signal_compatibility_id", out var compat)
                    && compat.ValueKind == JsonValueKind.Number
                    && compat.TryGetInt32(out var compatId))
                {
                    hasHdr10Base = compatId is 1 or 2 or 4;
                }

                // Profiles 7 and 8 always carry a base layer whether or not the
                // compatibility field made it into the container.
                if (value is 7 or 8)
                {
                    hasHdr10Base = true;
                }

                return;
            }
        }

        /// <summary>Probes for black bars.</summary>
        /// <param name="mediaPath">The video file.</param>
        /// <param name="plan">The window being sampled.</param>
        /// <param name="info">Source dimensions, used to sanity-check the result.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The crop to apply, or null to use the full frame.</returns>
        public async Task<CropRect?> DetectCropAsync(
            string mediaPath,
            SamplePlan plan,
            VideoInfo info,
            CancellationToken cancellationToken)
        {
            var encoder = EncoderPath;

            if (encoder is null)
            {
                return null;
            }

            var result = await _runner.RunAsync(
                encoder,
                FfmpegArguments.BuildCropDetect(mediaPath, plan),
                cancellationToken).ConfigureAwait(false);

            // A non-zero exit is not treated as fatal. cropdetect writes its findings
            // as it goes, so a probe that failed at the end still produced usable
            // readings, and the alternative to a crop is sampling the full frame —
            // a slightly worse barcode, not a failure.
            var crop = CropDetectParser.SelectModal(result.StandardError, info.Width, info.Height);

            if (crop.HasValue)
            {
                _logger.LogDebug(
                    "Colorist: cropping {Crop} from {Width}x{Height} for {Path}",
                    crop.Value.ToFilter(),
                    info.Width,
                    info.Height,
                    mediaPath);
            }

            return crop;
        }

        /// <summary>Samples the file and reduces every frame to a colour.</summary>
        /// <param name="mediaPath">The video file.</param>
        /// <param name="plan">Window and stripe count.</param>
        /// <param name="crop">Crop to apply, if any.</param>
        /// <param name="keyframesOnly">Whether to decode only keyframes.</param>
        /// <param name="threads">Decoder thread cap.</param>
        /// <param name="toneMapping">Colour conversion to apply before sampling.</param>
        /// <param name="strategy">The colour strategy.</param>
        /// <param name="options">Colour knobs.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>One timed sample per decoded frame.</returns>
        public async Task<IReadOnlyList<TimedSample>> SampleAsync(
            string mediaPath,
            SamplePlan plan,
            CropRect? crop,
            bool keyframesOnly,
            int threads,
            ToneMapping toneMapping,
            IFrameColorStrategy strategy,
            ColorOptions options,
            CancellationToken cancellationToken)
        {
            var encoder = EncoderPath;

            if (encoder is null)
            {
                _logger.LogError("Colorist: Jellyfin reports no ffmpeg path; nothing can be sampled");
                return Array.Empty<TimedSample>();
            }

            var colours = new List<Rgb>(plan.Columns);
            var attempt = toneMapping;
            FfmpegResult result = default;

            // A ladder rather than one attempt, because both conversion chains depend
            // on optional ffmpeg components — libplacebo for Dolby Vision, zscale for
            // HDR10 — and when one is missing ffmpeg rejects the entire filter graph
            // and emits nothing at all. Jellyfin's own builds carry both, but the
            // encoder path can point at a distribution build that does not.
            //
            // Degrading costs a re-decode and yields a worse barcode. Not degrading
            // yields no barcode, and an item silently skipped forever is the harder
            // failure to notice.
            while (true)
            {
                result = await RunSampleAsync(
                    encoder, mediaPath, plan, crop, keyframesOnly, threads, attempt,
                    strategy, options, colours, cancellationToken).ConfigureAwait(false);

                if (colours.Count > 0)
                {
                    break;
                }

                var next = ToneMappingPolicy.Fallback(attempt);

                if (next is null)
                {
                    break;
                }

                _logger.LogWarning(
                    "Colorist: {Mode} conversion produced no frames for {Path} (ffmpeg exited {Code}); "
                    + "falling back to {Next}. The ffmpeg in use may lack the required filter. Error: {Error}",
                    attempt,
                    mediaPath,
                    result.ExitCode,
                    next.Value,
                    Tail(result.StandardError));

                attempt = next.Value;
            }

            if (colours.Count == 0)
            {
                _logger.LogWarning(
                    "Colorist: ffmpeg exited {Code} and produced no frames for {Path}. Error output: {Error}",
                    result.ExitCode,
                    mediaPath,
                    Tail(result.StandardError));

                return Array.Empty<TimedSample>();
            }

            if (attempt != toneMapping)
            {
                _logger.LogInformation(
                    "Colorist: {Path} was sampled with {Actual} rather than {Wanted}; colours may be off",
                    mediaPath,
                    attempt,
                    toneMapping);
            }

            // Positions are arithmetic, not reported. Every invocation now carries an
            // fps filter, so the frames arriving on the pipe are evenly spaced across
            // the sampled window by construction and frame N sits at N × interval.
            //
            // This replaced a showinfo-parsing path that paired each frame with a
            // timestamp scraped from stderr, which was only ever needed because
            // keyframes arrive at irregular positions. Capping the rate removes the
            // irregularity and several thousand lines of stderr with it.
            return WithUniformTimes(colours, plan);
        }

        /// <summary>
        /// Reads whole frames off the pipe and reduces each one immediately.
        /// </summary>
        /// <remarks>
        /// Frames are consumed as they arrive rather than buffered. A three-hour film
        /// sampled on keyframes is several thousand frames; holding them would be
        /// hundreds of megabytes for data that collapses to three bytes each. This way
        /// the peak cost is one frame, whatever the runtime.
        /// </remarks>
        private static async Task ConsumeFramesAsync(
            Stream stdout,
            IFrameColorStrategy strategy,
            ColorOptions options,
            List<Rgb> colours,
            CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(FfmpegArguments.SampleFrameBytes);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var filled = 0;

                    // Filled with a loop rather than a single read because a pipe
                    // returns whatever happens to be available, which is rarely a
                    // whole frame. Reducing a partial buffer would mean measuring the
                    // top of one frame against stale bytes from the last.
                    while (filled < FfmpegArguments.SampleFrameBytes)
                    {
                        var read = await stdout.ReadAsync(
                            buffer.AsMemory(filled, FfmpegArguments.SampleFrameBytes - filled),
                            cancellationToken).ConfigureAwait(false);

                        if (read == 0)
                        {
                            break;
                        }

                        filled += read;
                    }

                    // A short read at the end is a truncated final frame — ffmpeg was
                    // killed, or the file ends mid-frame. Dropping it loses one stripe
                    // out of a thousand; keeping it would put a band of garbage at the
                    // end of the image.
                    if (filled < FfmpegArguments.SampleFrameBytes)
                    {
                        break;
                    }

                    colours.Add(strategy.Represent(
                        buffer.AsSpan(0, FfmpegArguments.SampleFrameBytes),
                        options));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task<FfmpegResult> RunSampleAsync(
            string encoder,
            string mediaPath,
            SamplePlan plan,
            CropRect? crop,
            bool keyframesOnly,
            int threads,
            ToneMapping toneMapping,
            IFrameColorStrategy strategy,
            ColorOptions options,
            List<Rgb> colours,
            CancellationToken cancellationToken)
        {
            // Cleared rather than reused, so a retry after a chain that emitted a few
            // frames before failing cannot leave those frames stuck at the head of the
            // strip, mixing two different colour conversions into one image.
            colours.Clear();

            var arguments = FfmpegArguments.BuildSample(
                mediaPath, plan, crop, keyframesOnly, threads, toneMapping);

            return await _runner.RunAsync(
                encoder,
                arguments,
                (stdout, token) => ConsumeFramesAsync(stdout, strategy, options, colours, token),
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The last few lines of ffmpeg's stderr, for a log message.</summary>
        /// <remarks>
        /// Truncated because a failing filter graph still produces a banner and a full
        /// stream dump, and the useful sentence is always the last one.
        /// </remarks>
        private static string Tail(string? stderr)
        {
            if (string.IsNullOrWhiteSpace(stderr))
            {
                return "(none)";
            }

            var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return string.Join(" | ", lines[Math.Max(0, lines.Length - 3)..]);
        }

        private static IReadOnlyList<TimedSample> WithUniformTimes(List<Rgb> colours, SamplePlan plan)
        {
            var samples = new List<TimedSample>(colours.Count);
            var interval = plan.DurationSeconds / Math.Max(1, colours.Count);

            for (var i = 0; i < colours.Count; i++)
            {
                samples.Add(new TimedSample(i * interval, colours[i]));
            }

            return samples;
        }

        private static string? Nullable(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
