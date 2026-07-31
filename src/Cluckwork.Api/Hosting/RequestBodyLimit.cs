namespace Cluckwork.Api.Hosting;

using Microsoft.AspNetCore.Http.Features;

// #309 — a per-endpoint request-body byte cap that rejects an oversized body
// BEFORE the framework binds it (and, for the auth endpoints, before the
// password reaches the PBKDF2 hasher).
//
// Why middleware and not an endpoint filter: a minimal-API JSON parameter is
// bound (the whole body buffered + deserialized) BEFORE endpoint filters run,
// so a filter is already too late. Routing runs at the start of the
// WebApplication pipeline, so a middleware placed after it sees the matched
// endpoint's metadata and can cap the body ahead of binding.

// Marker attached to an endpoint via WithMaxRequestBodyBytes; read by the
// middleware below.
public sealed record MaxRequestBodyBytesMetadata(long Bytes);

public static class RequestBodyLimit
{
    // Cap the request body for THIS endpoint. Works on both a single route
    // (RouteHandlerBuilder) and a whole group (RouteGroupBuilder) — both are
    // IEndpointConventionBuilder.
    public static TBuilder WithMaxRequestBodyBytes<TBuilder>(this TBuilder builder, long bytes)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new MaxRequestBodyBytesMetadata(bytes));

    // Enforces the per-endpoint cap. Registered after routing (endpoint
    // metadata available), after auth's rate limiter (#143) and Serilog
    // request logging (#214) — see the call site in Program.cs for why — and
    // still ahead of auth/tenant/idempotency/binding/the PBKDF2 hasher.
    public static IApplicationBuilder UseCluckworkRequestBodyLimit(this IApplicationBuilder app) =>
        app.Use(static async (context, next) =>
        {
            var meta = context.GetEndpoint()?.Metadata.GetMetadata<MaxRequestBodyBytesMetadata>();
            if (meta is null)
            {
                await next();
                return;
            }

            // (1) Kestrel transport-level cutoff: also stops a chunked/streamed
            // body with no declared length at the transport, before ASP.NET
            // reads a byte. Best-effort only — the feature is ABSENT under the
            // in-memory TestServer and turns read-only once the body is touched
            // — so (2)+(3) are the portable guarantee, not this.
            var sizeLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeLimit is { IsReadOnly: false })
                sizeLimit.MaxRequestBodySize = meta.Bytes;

            // (2) A DECLARED oversize is refused HERE, before a byte of the body
            // is read / bound / hashed. Content-Length is only a claim, which is
            // why (3) below also caps the read for a chunked body or a lying one.
            if (context.Request.ContentLength > meta.Bytes)
            {
                await WriteBodyTooLargeAsync(context, meta.Bytes);
                return;
            }

            // (3) Portable read cap for a chunked/streamed body (no declared
            // length) or a lying Content-Length: aborted at the cap DURING
            // binding — before the whole oversized body is buffered or the
            // credential reaches the hasher. Hand-rolled because the framework's
            // own enforcement ((1)) is Kestrel-only; this holds under any server
            // (and the test host).
            context.Request.Body = new ByteCappedRequestStream(context.Request.Body, meta.Bytes);

            await next();

            // Empirically measured: for an endpoint with a JSON-bound parameter
            // (login, change-password, create-user, set-password — every auth
            // endpoint except refresh), when ByteCappedRequestStream throws its
            // 413 DURING minimal-API's generated JSON-binding code, that
            // generated code CATCHES the BadHttpRequestException itself, sets
            // Response.StatusCode = 413, and returns WITHOUT rethrowing. It
            // never reaches UseExceptionHandler/`/error`, so without this check
            // the client gets a bare 413 with Content-Length: 0 and no
            // Content-Type — a different response shape than the declared-
            // Content-Length branch above (which writes a full ProblemDetails
            // body itself). The response has NOT started at this point on both
            // the anonymous and idempotency-buffered paths, so it's still safe
            // to write here. Recognise that shape and write the SAME canonical
            // problem body, giving every 413 this middleware is responsible for
            // one response contract.
            if (context.Response.StatusCode == StatusCodes.Status413PayloadTooLarge
                && !context.Response.HasStarted)
                await WriteBodyTooLargeAsync(context, meta.Bytes);
        });

    // Single write site for a body-too-large response, reached from both the
    // declared-length short-circuit above and the post-`next()` recovery for a
    // framework-swallowed streamed/chunked 413. Results.Problem (rather than a
    // hand-rolled anonymous-object write) keeps this byte-identical to what
    // `/error` emits for a BadHttpRequestException (see Program.cs's `/error`
    // handler) — same `type` URI, same shape, one contract for every 413.
    private static Task WriteBodyTooLargeAsync(HttpContext context, long cap) =>
        Results.Problem(
            detail: $"The request body exceeds the {cap}-byte limit for this endpoint.",
            statusCode: StatusCodes.Status413PayloadTooLarge,
            title: "Invalid request body")
        .ExecuteAsync(context);
}

// Read-only wrapper that counts bytes read from the inner request body and, once
// the total exceeds the cap, throws a 413 BadHttpRequestException — the same
// exception Kestrel raises for an over-limit body, so the /error handler maps it
// to a 413 ProblemDetails (when the throw isn't swallowed by binding — see the
// middleware above for the case where it is).
internal sealed class ByteCappedRequestStream(Stream inner, long cap) : Stream
{
    private long _read;

    // Clamp the caller's requested length to at most the remaining allowance
    // (+1) BEFORE delegating to the inner stream. Without this, a single
    // Read/ReadAsync call can hand back an entire pipe segment (measured: up to
    // 4096+ bytes) before Count() below ever runs — on a transport without
    // IHttpMaxRequestBodySizeFeature enforcement (the in-memory TestServer, or a
    // lying/absent Content-Length under load-balancer buffering) that lets a
    // single read overshoot the cap by a full buffer instead of stopping at it.
    // The +1 (not exactly the remaining allowance) means a read that lands
    // EXACTLY on the boundary still pulls one extra byte, so Count() below can
    // detect the overflow on this call rather than silently accepting a read
    // that stops precisely at the cap and only failing on the next one.
    private int Clamp(int requested)
    {
        var remaining = cap - _read + 1;
        return remaining < requested ? (int)remaining : requested;
    }

    private int Count(int n)
    {
        _read += n;
        if (_read > cap)
            throw new BadHttpRequestException(
                "Request body too large.", StatusCodes.Status413PayloadTooLarge);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer[..Clamp(buffer.Length)], cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        Count(inner.Read(buffer, offset, Clamp(count)));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer[..Clamp(buffer.Length)]));

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
