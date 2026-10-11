using System.Security.Cryptography;
using System.Text;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics;

namespace Cluckwork.Api.Middleware;

// #307 — the HTTP adapter of the claim/lease protocol in IdempotentUnit. It
// decides which writes are tracked, derives the claim's scope from the
// request, runs the downstream pipeline as the operation (a 2xx is a
// success), and puts the outcome back on the wire.
public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    private const string HeaderName = "Idempotency-Key";

    // #165 — routes whose RESPONSE must never be cached or replayed. A record
    // stores only status/content-type/body, so caching one of these would both
    // (a) persist that body — here a live access token — in idempotency_records,
    // and (b) replay it WITHOUT Set-Cookie, handing the client a fresh access
    // token while its refresh cookie stays the old, now-revoked one: a session
    // that dies silently ~15 minutes later. Caching buys nothing here anyway —
    // a password change is self-invalidating, because a replayed request's
    // current-password no longer matches.
    private static readonly string[] ResponseNotCacheable =
    [
        "/api/v1/auth/change-password",
        // #308 — same reasoning as change-password: the response body is a
        // live, single-use step-up grant. Caching/replaying it would hand back
        // an already-consumed (or since-expired) token instead of a fresh one.
        "/api/v1/auth/step-up",
        // #336 review — logout is a pure side effect (its body is an empty 204),
        // so replaying a stored response would return "success" while revoking
        // NOTHING: the caller believes the session ended when it did not. It is
        // also idempotent by construction, so it needs no replay protection.
        //
        // Being on this list is load-bearing for a second reason: the exemption
        // is checked BEFORE the tenant gate below, and logout became able to
        // carry a bearer (#336 — it must revoke the access-token user's step-up
        // grants, not only the cookie owner's). Without this entry that bearer
        // would resolve a tenant and every authenticated logout would 400 for a
        // missing Idempotency-Key, i.e. the SPA could no longer log out.
        "/api/v1/auth/logout",
        // #532 — login and refresh both return a LIVE ACCESS TOKEN in the body.
        // Both endpoints blank their ambient principal via
        // IgnoresAmbientPrincipalAttribute, so neither resolves a tenant and
        // the gate below skips them — and being on this list is what keeps that
        // true if the marker is ever removed. Being here also means a future
        // change that resolves a tenant on either path cannot silently start
        // persisting a live credential into idempotency_records, keyed by
        // account rather than by user and replayable by anyone in that account.
        "/api/v1/auth/login",
        "/api/v1/auth/refresh",
    ];

    public async Task InvokeAsync(
        HttpContext context, IdempotentUnit unit, TenantContext tenant, CurrentUserContext user)
    {
        // #345 — UseExceptionHandler (Program.cs) does not just render a body: on
        // an unhandled exception it rewrites Request.Path to "/error" and
        // RE-EXECUTES the whole downstream pipeline, this middleware included. On
        // that second pass the method is still the original write verb and the
        // tenant still resolves from the original request's bearer token, but the
        // PATH is now "/error" — so every path-derived decision below is made
        // about a request the client never sent. Two measured consequences:
        //
        //   (a) the ResponseNotCacheable exemption is matched on Request.Path, so
        //       it stops matching: a /auth/change-password that throws re-enters
        //       here as "/error", carries no Idempotency-Key (correctly — it is
        //       exempt), and gets this middleware's own 400
        //       "Idempotency-Key header is required" INSTEAD of the exception's
        //       real mapped status. A 500 was being served to the client as a 400.
        //
        //   (b) for a tracked write the re-entry claims a SECOND, "/error"-scoped
        //       row (EndpointHash covers the path, so it does NOT collide with
        //       the original request's claim — that one is already released by
        //       the catch below). Harmless in the happy case, but it makes
        //       rendering an error a DATABASE WRITE: when the fault IS the
        //       database, the claim INSERT throws too, the exception handler
        //       itself fails, and the client gets no ProblemDetails at all —
        //       just a dropped/500-with-no-body connection.
        //
        // Idempotency has nothing to contribute to a re-execution: the client's
        // request was already claimed, executed and released (or replayed) by the
        // first pass, and "/error" is a synthetic path no client can retry against.
        // So skip entirely — do not merely widen the exemption match.
        if (context.Features.Get<IExceptionHandlerFeature>() is not null)
        {
            await next(context);
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method)
            && !HttpMethods.IsPut(context.Request.Method)
            && !HttpMethods.IsPatch(context.Request.Method)
            && !HttpMethods.IsDelete(context.Request.Method))
        {
            await next(context);
            return;
        }

        // #806 — /mcp carries every MCP message as a POST, initialize and tools/list
        // included, and no MCP client sends an Idempotency-Key. A write tool keys its own
        // claim on the tool and its arguments (#809). Matched on endpoint metadata, never
        // a path, so the bearer set is what the endpoint table says it is.
        if (context.GetEndpoint()?.Metadata.GetMetadata<HandlesOwnIdempotencyAttribute>() is not null)
        {
            await next(context);
            return;
        }

        if (ResponseNotCacheable.Any(p =>
                context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        if (!tenant.IsResolved)
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var rawKey) || string.IsNullOrWhiteSpace(rawKey))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://cluckwork.local/problems/idempotency-key-required",
                title = "Idempotency-Key header is required for write requests.",
                status = StatusCodes.Status400BadRequest
            });
            return;
        }

        var accountId = tenant.AccountId;
        var endpointHash = Sha256($"{context.Request.Method}:{context.Request.Path}");
        // User-scope the idempotency key ONLY for the per-user /me endpoints (#45): two
        // users in one account presenting the same key on their own /me write must each
        // execute. Every other endpoint operates on shared account data and keeps the
        // account-only key, so no existing record's hash changes — a same-user retry
        // spanning a deployment still replays, with no cross-deploy re-execution window
        // on account-scoped writes (sales, payments, inventory, …).
        var userScoped = user.IsResolved
            && context.Request.Path.StartsWithSegments("/api/v1/me", StringComparison.OrdinalIgnoreCase);
        var keyHash = userScoped ? Sha256($"{user.UserId}:{rawKey}") : Sha256(rawKey.ToString());
        var requestHash = await ComputeRequestHashAsync(context.Request, context.RequestAborted);

        var scope = new IdempotencyScope(accountId, endpointHash, keyHash, requestHash);

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        var result = await unit.RunAsync(scope, async () =>
        {
            context.Response.Body = buffer;
            try
            {
                await next(context);
            }
            finally
            {
                context.Response.Body = originalBody;
            }

            buffer.Position = 0;
            var body = await new StreamReader(buffer).ReadToEndAsync(context.RequestAborted);
            var statusCode = context.Response.StatusCode;
            return new IdempotentResponse(
                statusCode is >= 200 and < 300, statusCode, context.Response.ContentType, body);
        }, context.RequestAborted);

        switch (result)
        {
            case IdempotentResult.Executed(var response):
                await EmitAsync(context, response);
                break;
            case IdempotentResult.Replayed(var response):
                await EmitAsync(context, response);
                break;
            case IdempotentResult.KeyReused:
                await WriteProblemAsync(context, StatusCodes.Status409Conflict,
                    "https://cluckwork.local/problems/idempotency-key-conflict",
                    "Idempotency-Key was already used with a different request.");
                break;
            default:
                await WriteProblemAsync(context, StatusCodes.Status409Conflict,
                    "https://cluckwork.local/problems/idempotency-in-progress",
                    "A request with this Idempotency-Key is still being processed. Retry shortly.");
                break;
        }
    }

    // #340 — the single place this middleware puts a buffered or replayed
    // response back on the wire. It exists because there are THREE of them
    // (publish, non-2xx echo, replay) and they must not drift: each one has to
    // respect the statuses that carry no message body.
    //
    // Kestrel enforces that rule on ANY write once the status is set, and
    // HttpResponseWritingExtensions.WriteAsync("") is still a write — it calls
    // BodyWriter.GetSpan(...) then Advance(0), and HttpProtocol.Advance throws
    // "Writing to the response body is invalid for responses with status code
    // 204." So an empty echo is not a harmless no-op; before #307 the echo was
    // buffer.CopyToAsync(originalBody), which really did write nothing for an
    // empty buffer, and that is the only reason this never surfaced earlier.
    //
    // The failure is also invisible from the client side: WriteAsync starts the
    // response before it writes, so the 204 and its headers are already on the
    // wire when Advance throws — the caller sees a valid 204 while the server
    // logs an unhandled exception and the connection dies. Assert on the
    // server-side completion log, not the status code (see
    // KestrelResponseWriteTests).
    private static async Task EmitAsync(HttpContext context, IdempotentResponse response)
    {
        context.Response.StatusCode = response.StatusCode;
        if (!CanHaveBody(response.StatusCode)) return;
        if (!string.IsNullOrWhiteSpace(response.ContentType)) context.Response.ContentType = response.ContentType;
        await context.Response.WriteAsync(response.Body, context.RequestAborted);
    }

    // RFC 9110 §15.3.5 / §15.3.6 / §15.4.5 — 204, 205 and 304 responses never
    // carry a message body.
    private static bool CanHaveBody(int statusCode) => statusCode
        is not StatusCodes.Status204NoContent
        and not StatusCodes.Status205ResetContent
        and not StatusCodes.Status304NotModified;

    private static async Task WriteProblemAsync(HttpContext context, int status, string type, string title)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { type, title, status });
    }

    // Hashes the raw request body (buffered + rewound so the real model
    // binder downstream still reads it). Bounded by whatever body-size cap
    // already applies ahead of this middleware (Kestrel's default, or the
    // #309 per-endpoint cap) — no additional limit needed here.
    //
    // #1148 — a write naming its target in the query (Disconnect's ?clientId=)
    // hashes the query too: one key reused for another app is a conflict, not a
    // replay of the first app's 204. Without a query the hash is unchanged.
    private static async Task<string> ComputeRequestHashAsync(HttpRequest request, CancellationToken ct)
    {
        request.EnableBuffering();
        request.Body.Position = 0;
        using var ms = new MemoryStream();
        if (request.QueryString.HasValue)
            ms.Write(Encoding.UTF8.GetBytes(request.QueryString.Value + "\n"));
        await request.Body.CopyToAsync(ms, ct);
        request.Body.Position = 0;
        return Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

// #806 — marks an endpoint IdempotencyMiddleware skips because the endpoint keys its own
// writes. Only /mcp carries it; McpEndpointTests pins that set.
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class HandlesOwnIdempotencyAttribute : Attribute;
