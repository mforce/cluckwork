namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Jobs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #270 — refresh_tokens grows without bound: every login/refresh inserts a row
// and rotation keeps inserting, but nothing ever deletes an expired/revoked
// row. RefreshTokenPurgeSweep (another DurableJobWorker sweep, modeled on
// DailyEntryLockSweep) purges rows whose OWN ExpiresAt — untouched by
// rotation, see RefreshToken.ExpiresAt — is older than
// RefreshTokenPurgeSweep.PurgeGrace. Keyed on ExpiresAt, never on
// RevokedAt/CreatedAt: a token rotated on day one of its life is retained for
// its full remaining nominal lifetime, exactly as long as
// IdentityProvider.RefreshAsync's reuse-detection (the RevokedAt-set branch,
// which never itself consults ExpiresAt) needs the row to survive a replay.
[Collection(IntegrationCollection.Name)]
public sealed class RefreshTokenPurgeSweepTests(CluckworkWebApplicationFactory factory)
{
    private static readonly WebApplicationFactoryClientOptions Cookieless =
        new() { HandleCookies = false };

    private static RefreshToken NewToken(
        DateTimeOffset expiresAt,
        DateTimeOffset? revokedAt = null,
        Guid? accountId = null,
        Guid? userId = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId ?? Guid.NewGuid(),
        AccountId = accountId ?? Guid.NewGuid(),
        // 64 hex chars — matches the real TokenHash shape (SHA-256 hex) and stays
        // unique across rows in one test (the column carries a unique index).
        TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-45),
        ExpiresAt = expiresAt,
        RevokedAt = revokedAt,
        ReplacedByTokenHash = revokedAt is null
            ? null
            : Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
    };

    // #546 — seeded through an UNRESOLVED tenant, deliberately.
    //
    // These fixtures span several accounts on purpose (Sweep_IsTenantSafeAndIdempotent
    // seeds two), and RefreshToken carries an AccountId while being deliberately NOT
    // tenant-query-filtered — the sweep is filter-free by design (see the header).
    // Seeding them under a RESOLVED tenant therefore writes rows belonging to other
    // accounts, which TenantStampInterceptor now refuses, correctly.
    //
    // The previous throwaway `WithTenantScopeAsync(Guid.NewGuid(), ...)` claimed a
    // tenant these rows do not belong to. An unresolved scope states the truth
    // instead: this is the same path the seeders and one-shot CLI verbs take, where
    // the write guard is deliberately inert.
    private async Task SeedAsync(params RefreshToken[] tokens)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.RefreshTokens.AddRange(tokens);
        await db.SaveChangesAsync();
    }

    private Task RunSweepAsync() =>
        factory.Services.GetRequiredService<RefreshTokenPurgeSweep>().RunAsync(CancellationToken.None);

    private Task<bool> ExistsAsync(Guid id) =>
        factory.WithTenantScopeAsync(Guid.NewGuid(), db => db.RefreshTokens.AnyAsync(t => t.Id == id));

    [Fact]
    public async Task Sweep_CollectsAgedRows_ExpiredAndRevoked()
    {
        var now = DateTimeOffset.UtcNow;
        var agedActive = NewToken(expiresAt: now - RefreshTokenPurgeSweep.PurgeGrace - TimeSpan.FromHours(1));
        var agedRevoked = NewToken(
            expiresAt: now - RefreshTokenPurgeSweep.PurgeGrace - TimeSpan.FromHours(1),
            revokedAt: now - RefreshTokenPurgeSweep.PurgeGrace - TimeSpan.FromHours(2));
        await SeedAsync(agedActive, agedRevoked);

        await RunSweepAsync();

        Assert.False(await ExistsAsync(agedActive.Id));
        Assert.False(await ExistsAsync(agedRevoked.Id));
    }

    [Fact]
    public async Task Sweep_LeavesActiveTokensUntouched_NearAndFarFromExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var farFuture = NewToken(expiresAt: now.AddDays(30));
        var aboutToExpire = NewToken(expiresAt: now.AddSeconds(5));
        // Expired for refresh purposes but still WITHIN the purge grace window —
        // not yet "aged" enough to collect (boundary, retained side).
        var withinGrace = NewToken(expiresAt: now - RefreshTokenPurgeSweep.PurgeGrace + TimeSpan.FromHours(1));
        await SeedAsync(farFuture, aboutToExpire, withinGrace);

        await RunSweepAsync();

        Assert.True(await ExistsAsync(farFuture.Id));
        Assert.True(await ExistsAsync(aboutToExpire.Id));
        Assert.True(await ExistsAsync(withinGrace.Id));
    }

    // The #270 correctness constraint: a revoked token keeps the ExpiresAt it
    // was minted with, so a rotation that happened moments ago must NOT make
    // the row purge-eligible just because it is revoked — only its own (still
    // distant) ExpiresAt controls that. If this regresses to a RevokedAt-age
    // predicate, a freshly-rotated row would purge immediately.
    [Fact]
    public async Task Sweep_LeavesRecentlyRevokedToken_WhoseExpiryIsStillFarOut()
    {
        var now = DateTimeOffset.UtcNow;
        var justRevoked = NewToken(expiresAt: now.AddDays(29), revokedAt: now);
        await SeedAsync(justRevoked);

        await RunSweepAsync();

        Assert.True(await ExistsAsync(justRevoked.Id));
    }

    // The load-bearing correctness test: run a real login → rotate → rotate
    // chain (so the FIRST token's replacement is itself already rotated away —
    // the #176 genuine-replay shape, not the benign grace-retry shape), sweep
    // in between, then prove a replay of the now-superseded token is STILL
    // detected as theft (whole family revoked) — not silently treated as an
    // unknown token because the sweep purged its row.
    [Fact]
    public async Task Sweep_DoesNotBreakReuseDetection_ReplayAfterChainMovedOnStillRevokesFamily()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateClient(Cookieless);

        var initial = await factory.LoginAsync(email);
        var r1 = await RefreshAsync(client, initial.RefreshToken, accountId);   // initial → r1
        var live = await RefreshAsync(client, r1.RefreshToken, accountId);      // r1 → live (r1 no longer the tip)

        await RunSweepAsync(); // must not disturb the reuse-detection lineage

        var replay = await client.PostRefreshAsync(initial.RefreshToken, expectedAccount: accountId.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // Cascade: the still-live tip is dead too — the whole session torn down.
        var afterCascade = await client.PostRefreshAsync(live.RefreshToken, expectedAccount: accountId.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, afterCascade.StatusCode);
    }

    private static async Task<TokenPairDto> RefreshAsync(HttpClient client, string refreshToken, Guid accountId)
    {
        var response = await client.PostRefreshAsync(refreshToken, expectedAccount: accountId.ToString());
        response.EnsureSuccessStatusCode();
        return await TestHarness.ReadTokensAsync(response);
    }

    [Fact]
    public async Task Sweep_IsTenantSafeAndIdempotent()
    {
        var now = DateTimeOffset.UtcNow;
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var agedA = NewToken(
            expiresAt: now - RefreshTokenPurgeSweep.PurgeGrace - TimeSpan.FromHours(1), accountId: accountA);
        var freshA = NewToken(expiresAt: now.AddDays(10), accountId: accountA);
        var agedB = NewToken(
            expiresAt: now - RefreshTokenPurgeSweep.PurgeGrace - TimeSpan.FromHours(1), accountId: accountB);
        var freshB = NewToken(expiresAt: now.AddDays(10), accountId: accountB);
        await SeedAsync(agedA, freshA, agedB, freshB);

        await RunSweepAsync();
        await RunSweepAsync(); // idempotent — a second run must not throw or touch survivors

        Assert.False(await ExistsAsync(agedA.Id));
        Assert.False(await ExistsAsync(agedB.Id));
        Assert.True(await ExistsAsync(freshA.Id));
        Assert.True(await ExistsAsync(freshB.Id));
    }

    // #270 review — the delete is batched (the first sweep after this ships is
    // the big one, and an unbounded ExecuteDelete would take months of backlog
    // in a single statement). Batching is only correct if a backlog larger than
    // one batch still fully drains, so seed across the boundary and prove it.
    [Fact]
    public async Task Sweep_DrainsABacklogLargerThanOneBatch()
    {
        var now = DateTimeOffset.UtcNow;
        var aged = Enumerable.Range(0, RefreshTokenPurgeSweep.BatchSizeForTests + 25)
            .Select(_ => NewToken(
                expiresAt: now - RefreshTokenPurgeSweep.PurgeGrace - TimeSpan.FromHours(1)))
            .ToArray();
        var survivor = NewToken(expiresAt: now.AddDays(10));
        await SeedAsync([.. aged, survivor]);

        await RunSweepAsync();

        foreach (var token in aged)
            Assert.False(await ExistsAsync(token.Id), "every aged row should drain, not just the first batch");
        Assert.True(await ExistsAsync(survivor.Id));
    }
}
