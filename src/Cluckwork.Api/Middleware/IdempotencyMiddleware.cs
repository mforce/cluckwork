namespace Cluckwork.Api.Middleware;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    private const string HeaderName = "Idempotency-Key";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

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
    ];

    public async Task InvokeAsync(
        HttpContext context, AppDbContext db, TenantContext tenant, CurrentUserContext user)
    {
        if (!HttpMethods.IsPost(context.Request.Method)
            && !HttpMethods.IsPut(context.Request.Method)
            && !HttpMethods.IsPatch(context.Request.Method)
            && !HttpMethods.IsDelete(context.Request.Method))
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
        var lockKey = $"{tenant.AccountId}:{endpointHash}:{keyHash}";
        var semaphore = Locks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(context.RequestAborted);
        try
        {
            var existing = await db.IdempotencyRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(r =>
                    r.AccountId == tenant.AccountId
                    && r.EndpointHash == endpointHash
                    && r.IdempotencyKeyHash == keyHash,
                    context.RequestAborted);

            if (existing is not null)
            {
                context.Response.StatusCode = existing.StatusCode;
                if (!string.IsNullOrWhiteSpace(existing.ContentType))
                    context.Response.ContentType = existing.ContentType;
                await context.Response.WriteAsync(existing.ResponseBody, context.RequestAborted);
                return;
            }

            var originalBody = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;

            try
            {
                await next(context);

                buffer.Position = 0;
                var body = await new StreamReader(buffer).ReadToEndAsync(context.RequestAborted);

                if (context.Response.StatusCode is >= 200 and < 300)
                {
                    db.IdempotencyRecords.Add(new IdempotencyRecord
                    {
                        Id = Guid.NewGuid(),
                        AccountId = tenant.AccountId,
                        EndpointHash = endpointHash,
                        IdempotencyKeyHash = keyHash,
                        StatusCode = context.Response.StatusCode,
                        ContentType = context.Response.ContentType,
                        ResponseBody = body,
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                    try
                    {
                        // Persist the replay record even if the client disconnects after
                        // the handler has committed its side effects. Otherwise a retry
                        // with the same key could execute the operation again.
                        await db.SaveChangesAsync(CancellationToken.None);
                    }
                    catch (DbUpdateException)
                    {
                        db.ChangeTracker.Clear();
                        var replay = await db.IdempotencyRecords
                            .AsNoTracking()
                            .FirstOrDefaultAsync(r =>
                                r.AccountId == tenant.AccountId
                                && r.EndpointHash == endpointHash
                                && r.IdempotencyKeyHash == keyHash,
                                CancellationToken.None);

                        if (replay is not null)
                        {
                            context.Response.StatusCode = replay.StatusCode;
                            context.Response.ContentType = replay.ContentType;
                            buffer.SetLength(0);
                            await using var writer = new StreamWriter(buffer, leaveOpen: true);
                            await writer.WriteAsync(replay.ResponseBody);
                            await writer.FlushAsync();
                        }
                    }
                }

                buffer.Position = 0;
                await buffer.CopyToAsync(originalBody, context.RequestAborted);
            }
            finally
            {
                context.Response.Body = originalBody;
            }
        }
        finally
        {
            semaphore.Release();
            // Remove after release so the dictionary doesn't grow without bound.
            // TryRemove(KVP) is atomic: only removes if this exact instance is still mapped.
            Locks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(lockKey, semaphore));
        }
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
