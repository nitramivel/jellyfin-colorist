using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Colorist.Services.Web
{
    /// <summary>
    /// Serves the client script and adds a tag for it to the web client's index page.
    /// </summary>
    /// <remarks>
    /// <b>Why middleware rather than the File Transformation plugin.</b> Middleware
    /// needs no second plugin installed, no cross-plugin API to call, and no ordering
    /// agreement with whatever else is patching web files. It is also the mechanism
    /// Jellyfin Enhanced uses, and the one Concierge settled on for the same reasons
    /// after the plan called for File Transformation — so it is proven on this server
    /// rather than merely documented.
    /// <para>
    /// The rewrite is the smallest one possible: a single script tag before
    /// <c>&lt;/body&gt;</c>, and nothing else touched. Every failure path restores the
    /// original response byte for byte. A plugin that can break the page it patches
    /// takes the entire web client down with it, and no barcode is worth that.
    /// </para>
    /// </remarks>
    public sealed partial class ScriptInjector : IStartupFilter
    {
        /// <summary>Where the script is served from.</summary>
        public const string ScriptPath = "/Colorist/client.js";

        private const string Marker = "id=\"colorist-client\"";

        private readonly ILogger<ScriptInjector> _logger;

        /// <summary>Initialises a new instance of the <see cref="ScriptInjector"/> class.</summary>
        /// <param name="logger">The logger.</param>
        public ScriptInjector(ILogger<ScriptInjector> logger)
        {
            _logger = logger;
        }

        [GeneratedRegex(@"var DISPLAY_HEIGHT = \d+;")]
        private static partial Regex DisplayHeightLine();

        /// <summary>
        /// The script's content hash, used as both cache-buster and entity tag.
        /// </summary>
        /// <remarks>
        /// Hashing the content rather than stamping the plugin version means the URL
        /// changes when and only when the script does — so an unchanged script still
        /// hits cache across an upgrade, and a changed one cannot be served stale.
        /// Without this, a browser that fetched the script once keeps running it after
        /// an upgrade, and the new version looks like it silently did nothing.
        /// <para>
        /// Recomputed per call rather than cached because the script carries settings:
        /// change the display height and the served file changes, so the URL has to
        /// change with it.
        /// </para>
        /// </remarks>
        private static string Fingerprint => Fingerprinted(Configured());

        /// <summary>Gets the URL the page should request.</summary>
        public static string VersionedScriptPath => ScriptPath + "?v=" + Fingerprint;

        /// <summary>Reads the script out of the assembly.</summary>
        /// <returns>The script, or empty when it is missing.</returns>
        public static string ReadScript()
        {
            var assembly = typeof(ScriptInjector).Assembly;

            // Found by suffix rather than by a constructed name. The manifest name is
            // derived from the root namespace and folder, so building it by string
            // surgery breaks silently the first time either is renamed — and the
            // symptom would be a detail page that simply never shows a barcode.
            var name = Array.Find(
                assembly.GetManifestResourceNames(),
                static n => n.EndsWith(".colorist.js", StringComparison.Ordinal));

            if (name is null)
            {
                return string.Empty;
            }

            using var stream = assembly.GetManifestResourceStream(name);

            if (stream is null)
            {
                return string.Empty;
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>The script as served, with the owner's settings substituted in.</summary>
        /// <returns>The configured script.</returns>
        public static string Configured()
        {
            var script = ReadScript();
            var configuration = Plugin.Instance?.Configuration;

            if (script.Length == 0 || configuration is null)
            {
                return script;
            }

            var height = Math.Clamp(configuration.DisplayHeight, 20, 400);

            return DisplayHeightLine().Replace(
                script,
                "var DISPLAY_HEIGHT = " + height.ToString(CultureInfo.InvariantCulture) + ";");
        }

        /// <summary>
        /// Inserts the script tag into a document.
        /// </summary>
        /// <param name="body">The document as served.</param>
        /// <returns>The patched document, or null to leave it exactly as it is.</returns>
        /// <remarks>
        /// Separated from the middleware so the decision can be tested without a
        /// server. Only a document that has a closing body tag and does not already
        /// carry the marker is touched — a reload must never stack tags up.
        /// </remarks>
        public static string? Patch(string body)
        {
            var close = body.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);

            if (close < 0 || body.Contains(Marker, StringComparison.Ordinal))
            {
                return null;
            }

            var tag = "<script id=\"colorist-client\" src=\"" + VersionedScriptPath + "\" defer></script>";

            return body[..close] + tag + body[close..];
        }

        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    if (context.Request.Path.Equals(ScriptPath, StringComparison.OrdinalIgnoreCase))
                    {
                        await ServeScriptAsync(context).ConfigureAwait(false);
                        return;
                    }

                    // The switch is checked here rather than at registration because a
                    // plugin's services are registered once at startup and the setting
                    // can be changed at any time. Turning it off takes effect on the
                    // next page load, not on the next server restart.
                    if (Plugin.Instance?.Configuration.ShowOnDetailPage != true
                        || !IsIndexRequest(context))
                    {
                        await nextMiddleware().ConfigureAwait(false);
                        return;
                    }

                    await InjectAsync(context, nextMiddleware).ConfigureAwait(false);
                });

                next(app);
            };
        }

        /// <summary>
        /// Whether this request is for the web client's index document.
        /// </summary>
        /// <remarks>
        /// Checked on the path rather than on the response content type, because
        /// buffering every response to find out would put the whole server's output
        /// through a memory stream to catch one document.
        /// </remarks>
        private static bool IsIndexRequest(HttpContext context)
        {
            var path = context.Request.Path.Value ?? string.Empty;

            return path.Length == 0
                || path.Equals("/", StringComparison.Ordinal)
                || path.Equals("/web", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/web/", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase);
        }

        private static string Fingerprinted(string script)
        {
            if (script.Length == 0)
            {
                return "0";
            }

            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(script));

            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        private async Task ServeScriptAsync(HttpContext context)
        {
            var script = Configured();

            if (script.Length == 0)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var tag = "\"" + Fingerprint + "\"";

            // "no-cache" means revalidate, not "never store". Paired with the entity
            // tag it costs one conditional request per page load and answers 304 for
            // the rest, which is the cheap half of never serving a stale client.
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.ETag = tag;

            if (context.Request.Headers.IfNoneMatch.ToString().Contains(tag, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }

            context.Response.ContentType = "application/javascript; charset=utf-8";
            await context.Response.WriteAsync(script).ConfigureAwait(false);
        }

        /// <summary>
        /// Buffers the index document and inserts one script tag before the closing
        /// body tag.
        /// </summary>
        /// <remarks>
        /// <b>The conditional request is handled here rather than upstream.</b>
        /// Jellyfin serves the index with an entity tag computed from the file on
        /// disk, which never changes when the plugin does. Left alone, a browser
        /// revalidates, gets a 304, and goes on using its cached copy of the patched
        /// page — complete with the script URL from whichever version it first saw.
        /// So the validators are stripped from the request to force a full body, and
        /// the response carries an entity tag over the patched document instead.
        /// </remarks>
        private async Task InjectAsync(HttpContext context, Func<Task> next)
        {
            var original = context.Response.Body;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;

            var wanted = context.Request.Headers.IfNoneMatch.ToString();
            context.Request.Headers.Remove("If-None-Match");
            context.Request.Headers.Remove("If-Modified-Since");

            try
            {
                await next().ConfigureAwait(false);

                buffer.Position = 0;
                var body = await new StreamReader(buffer).ReadToEndAsync().ConfigureAwait(false);

                var patched = Patch(body);

                if (patched is null)
                {
                    context.Response.Body = original;
                    buffer.Position = 0;
                    await buffer.CopyToAsync(original).ConfigureAwait(false);
                    return;
                }

                var bytes = Encoding.UTF8.GetBytes(patched);
                var etag = "\"" + Fingerprinted(patched) + "\"";

                context.Response.Body = original;
                context.Response.Headers.LastModified = default;
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers.ETag = etag;

                if (wanted.Contains(etag, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status304NotModified;
                    context.Response.ContentLength = null;
                    return;
                }

                context.Response.ContentLength = bytes.Length;
                await original.WriteAsync(bytes).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Colorist: could not add the client script; the page is served unchanged");

                context.Response.Body = original;

                try
                {
                    buffer.Position = 0;
                    await buffer.CopyToAsync(original).ConfigureAwait(false);
                }
                catch (Exception copyFailure)
                {
                    _logger.LogError(copyFailure, "Colorist: the original response could not be restored");
                }
            }
            finally
            {
                context.Response.Body = original;
            }
        }
    }
}
