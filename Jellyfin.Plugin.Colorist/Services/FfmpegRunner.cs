using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Colorist.Services
{
    /// <summary>The result of a run that produced no piped output.</summary>
    /// <param name="ExitCode">The process exit code.</param>
    /// <param name="StandardError">Everything the process wrote to stderr.</param>
    public readonly record struct FfmpegResult(int ExitCode, string StandardError);

    /// <summary>
    /// Runs ffmpeg, at low priority, streaming its stdout to a callback.
    /// </summary>
    /// <remarks>
    /// <b>This is the one class here that cannot be tested on the build machine</b> —
    /// there is no ffmpeg on it and no Jellyfin server. Everything it does with the
    /// arguments is decided in <c>Core/Sampling</c>, which is pure and covered; what
    /// is left here is process handling, and it is written defensively for that
    /// reason.
    /// </remarks>
    public sealed class FfmpegRunner
    {
        private readonly ILogger<FfmpegRunner> _logger;

        /// <summary>Initialises a new instance of the <see cref="FfmpegRunner"/> class.</summary>
        /// <param name="logger">The logger.</param>
        public FfmpegRunner(ILogger<FfmpegRunner> logger)
        {
            _logger = logger;
        }

        /// <summary>Runs a process and discards stdout, keeping stderr.</summary>
        /// <param name="executable">Full path to ffmpeg or ffprobe.</param>
        /// <param name="arguments">The command line.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Exit code and stderr.</returns>
        public Task<FfmpegResult> RunAsync(
            string executable,
            string arguments,
            CancellationToken cancellationToken) =>
            RunAsync(executable, arguments, null, cancellationToken);

        /// <summary>Runs a process, handing stdout to a reader.</summary>
        /// <param name="executable">Full path to ffmpeg or ffprobe.</param>
        /// <param name="arguments">The command line.</param>
        /// <param name="readStandardOutput">
        /// Consumes stdout. Must read to the end, or the child blocks on a full pipe.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Exit code and stderr.</returns>
        public async Task<FfmpegResult> RunAsync(
            string executable,
            string arguments,
            Func<Stream, CancellationToken, Task>? readStandardOutput,
            CancellationToken cancellationToken)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = false,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
                EnableRaisingEvents = true,
            };

            _logger.LogDebug("Colorist: {Executable} {Arguments}", executable, arguments);

            process.Start();

            TryLowerPriority(process);

            // stderr is drained on its own task throughout. ffmpeg writes progress and,
            // in the sampling invocation, one showinfo line per frame — thousands of
            // them. Left unread until the process exits, that fills the stderr pipe
            // buffer and ffmpeg blocks writing to it, forever, while we wait for it to
            // finish. It is a deadlock that only shows up on longer files.
            var stderrTask = ReadAllAsync(process.StandardError, cancellationToken);

            try
            {
                if (readStandardOutput is not null)
                {
                    await readStandardOutput(process.StandardOutput.BaseStream, cancellationToken)
                        .ConfigureAwait(false);
                }

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }

            var stderr = await stderrTask.ConfigureAwait(false);

            return new FfmpegResult(process.ExitCode, stderr);
        }

        /// <summary>
        /// Drops the child to below-normal scheduling priority.
        /// </summary>
        /// <remarks>
        /// <b>This is how Colorist stays out of a live transcode's way.</b> The
        /// obvious alternative — ask Jellyfin what is transcoding and pause — is not
        /// available: <c>ITranscodeManager</c> on 10.11 can look a job up by play
        /// session ID but cannot enumerate active jobs, and <c>ISessionManager</c>
        /// wants a user context that a scheduled task does not have. Rather than
        /// invent an interface, this hands the problem to the OS scheduler, which is
        /// better at it anyway: a barcode job soaks up idle CPU and yields it the
        /// instant a transcode wants it, with no polling and no policy.
        /// <para>
        /// Best-effort. Lowering priority can be refused, and a barcode at normal
        /// priority is a far better outcome than a failed run.
        /// </para>
        /// </remarks>
        private void TryLowerPriority(Process process)
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                          or PlatformNotSupportedException
                                          or System.ComponentModel.Win32Exception)
            {
                _logger.LogDebug(ex, "Colorist: could not lower ffmpeg priority; continuing at normal priority");
            }
        }

        private static async Task<string> ReadAllAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var builder = new StringBuilder();
            var buffer = new char[8192];

            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    builder.Append(buffer, 0, read);
                }
            }
            catch (OperationCanceledException)
            {
                // Whatever arrived before cancellation is still worth returning.
            }

            return builder.ToString();
        }

        private void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                          or NotSupportedException
                                          or System.ComponentModel.Win32Exception)
            {
                _logger.LogWarning(ex, "Colorist: could not stop an ffmpeg process");
            }
        }
    }
}
