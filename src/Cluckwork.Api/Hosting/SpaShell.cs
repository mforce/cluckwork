namespace Cluckwork.Api.Hosting;

using System.Text;
using Cluckwork.Api.Security;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

// #873 — the built SPA's index.html, served as a TEMPLATED response rather than
// as a static file, because one value in it has to match the response's own
// Content-Security-Policy header: the style nonce Emotion needs before MUI's
// runtime styles are allowed to apply (#674).
//
// WHY A SPLIT AT BOOT RATHER THAN A REPLACE PER REQUEST. The document never
// changes while the process lives — it is baked into the image — so the only
// per-request work is writing two frozen halves around a 24-character value.
// A per-request read or Replace would re-scan several kilobytes on every
// navigation for a result that cannot differ.
//
// WHY NO ETag AND NO Last-Modified. Every response now carries a different
// nonce, so a validator derived from the FILE would tell a client its stale
// copy is still fresh, and that copy names a nonce the new response's header
// does not admit — MUI would silently lose its styles on exactly the clients
// that cached best. #141's policy is otherwise unchanged: `no-cache`, never
// `immutable`, so the update-detection path the PWA relies on (#142) still
// revalidates on every navigation.
public sealed class SpaShell
{
    // Matched case-insensitively because HTML tag names are, even though Vite
    // emits a lower-case one and has no reason to stop.
    private const string HeadOpenTag = "<head>";

    private readonly string _head;
    private readonly string _tail;

    private SpaShell(string head, string tail)
    {
        _head = head;
        _tail = tail;
    }

    /// <summary>
    /// The shell for this environment, or <c>null</c> when there is no built SPA
    /// to serve — Development, where Vite serves the app, and the test host.
    /// </summary>
    public static SpaShell? Load(IWebHostEnvironment environment)
    {
        var webRoot = environment.WebRootPath;
        if (string.IsNullOrEmpty(webRoot))
            return null;

        var indexPath = Path.Combine(webRoot, "index.html");
        return File.Exists(indexPath) ? FromDocument(File.ReadAllText(indexPath)) : null;
    }

    /// <summary>Splits a document around the one place the nonce meta goes.</summary>
    /// <exception cref="InvalidOperationException">
    /// The document has no &lt;head&gt;. Thrown at boot, in the serving process
    /// only (a one-shot verb exits before the HTTP pipeline is built), because
    /// the alternative is a container that starts, passes its health probe and
    /// serves an app with no MUI styling at all — the #510 shape: a check that
    /// belongs at boot must not be deferred into a per-request surprise.
    /// </exception>
    public static SpaShell FromDocument(string document)
    {
        var head = document.IndexOf(HeadOpenTag, StringComparison.OrdinalIgnoreCase);
        if (head < 0)
            throw new InvalidOperationException(
                "The built SPA's index.html has no <head>, so the CSP nonce (#873) cannot be "
                + "written into it. Serving it untemplated would drop every MUI style silently.");

        var insertAt = head + HeadOpenTag.Length;
        return new SpaShell(document[..insertAt], document[insertAt..]);
    }

    /// <summary>Writes the shell carrying this response's own style nonce.</summary>
    public Task WriteAsync(HttpContext context)
    {
        // The SAME value the security-headers callback will put in the policy:
        // both go through GetOrCreateNonce, which mints once per request.
        var nonce = SecurityHeaders.GetOrCreateNonce(context);

        var response = context.Response;
        response.ContentType = "text/html; charset=utf-8";
        // Indexer, not TryAdd: this is the deliberate policy #312's default
        // defers to, exactly as the static-file callback's assignment was.
        response.Headers[HeaderNames.CacheControl] = StaticAssetCaching.AlwaysRevalidate;

        // The nonce is base64 of this process's own RNG output, so it carries no
        // character the attribute would have to escape.
        return response.WriteAsync(
            $"{_head}<meta name=\"{SecurityHeaders.NonceMetaName}\" content=\"{nonce}\">{_tail}",
            Encoding.UTF8);
    }
}

public static class SpaShellExtensions
{
    /// <summary>
    /// Answers <c>/</c> and <c>/index.html</c> with the templated shell.
    /// </summary>
    /// <remarks>
    /// Registered BEFORE the static-file middleware, which is the whole point:
    /// left to itself it would serve the untemplated file from wwwroot. Both
    /// paths matter and neither is redundant. <c>/</c> is what a person opens,
    /// and it used to be reached through UseDefaultFiles, which this replaces.
    /// <c>/index.html</c> is what WORKBOX fetches to precache, and the service
    /// worker then answers every navigation from that cached response (#142) —
    /// so an untemplated copy there would mean the app loses its MUI styling for
    /// every client that installed the worker, online and offline alike.
    /// </remarks>
    public static IApplicationBuilder UseSpaShell(this IApplicationBuilder app, SpaShell shell) =>
        app.Use(async (context, next) =>
        {
            // Ordinal, mirroring the case-sensitive file lookup this replaces on
            // Linux: /INDEX.HTML 404s here exactly as it did when the static
            // middleware owned the path.
            var path = context.Request.Path.Value;
            var method = context.Request.Method;
            // GET/HEAD only (#874 review, local Codex pass): the static-file
            // middleware this replaced only ever served GET/HEAD, and falling
            // through here (rather than answering 405 directly) lets the SAME
            // 405 come from the MapFallback registration in Program.cs, whose
            // HttpMethodMetadata is the one place that decision is made.
            if ((path == "/" || string.Equals(path, "/index.html", StringComparison.Ordinal))
                && (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)))
            {
                await shell.WriteAsync(context);
                return;
            }

            await next();
        });
}

/// <summary>
/// Hides the shell's own source document from static-file serving (#874 review,
/// local Codex pass).
/// </summary>
/// <remarks>
/// <see cref="SpaShellExtensions.UseSpaShell"/>'s exact-match check is a literal
/// compare against <c>Request.Path.Value</c>. Kestrel does not collapse a raw
/// "GET //index.html" request-target the way it collapses a "/./" segment (the
/// latter is resolved before the app ever sees it; the former is not), so that
/// request misses the exact match and falls through to <c>UseStaticFiles</c> —
/// whose <c>PhysicalFileProvider</c> still resolves it to the same on-disk
/// index.html, serving the untemplated build artifact with no nonce meta under a
/// CSP header that now requires one. Wrapping the file provider makes the
/// document unreachable through static serving no matter how many leading
/// slashes or what case the request spells it with; the request then falls
/// through further to <c>MapFallback</c>, which answers with the templated shell
/// exactly as it already does for any other client-side route.
/// </remarks>
public sealed class IndexHtmlHidingFileProvider(IFileProvider inner) : IFileProvider
{
    public IDirectoryContents GetDirectoryContents(string subpath) => inner.GetDirectoryContents(subpath);

    public IFileInfo GetFileInfo(string subpath)
    {
        // Split rather than compare the raw string: collapses any number of
        // leading or doubled slashes away, so "//index.html" and "/index.html"
        // resolve to the same single segment. Case-insensitive because the goal
        // is "this file is never servable raw" — not a second copy of the
        // case-sensitive Linux lookup UseSpaShell already relies on, so
        // /INDEX.HTML still 404s either way, just earlier.
        var segments = subpath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var isIndexHtml = segments is [var only] && string.Equals(only, "index.html", StringComparison.OrdinalIgnoreCase);
        return isIndexHtml ? new NotFoundFileInfo(subpath) : inner.GetFileInfo(subpath);
    }

    public IChangeToken Watch(string filter) => inner.Watch(filter);
}
