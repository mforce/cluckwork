namespace Cluckwork.Api.IntegrationTests;

using System.Data.Common;
using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

public sealed class RefreshGraceClockRaceFactory : CluckworkWebApplicationFactory
{
    public RefreshReadBarrierInterceptor Barrier { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Grace stays at its default (non-zero): these tests are about WHICH
        // instant the grace window is measured from, not about whether grace is
        // enabled — the disabled case is RefreshGraceDisabledTests' subject.
        builder.ConfigureTestServices(services =>
            services.AddDbContext<AppDbContext>((_, options) => options.AddInterceptors(Barrier)));
    }
}

// Parks the FIRST refresh-token lookup that runs after Arm(), immediately before
// PostgreSQL executes it. RefreshAsync reads the clock and then loads the
// presented token, so holding that load open lets a second request rotate the
// same token and commit while the first is still in flight — the read-after-
// commit interleaving that #468 hit intermittently on CI, made deterministic.
//
// Disarms on the first match (CompareExchange), so the concurrent winner's own
// lookup — and the grace-replacement lookup later in the same request — run
// through untouched.
public sealed class RefreshReadBarrierInterceptor : DbCommandInterceptor
{
    private TaskCompletionSource _reached = NewSignal();
    private TaskCompletionSource _release = NewSignal();
    private int _armed;

    public void Arm()
    {
        _reached = NewSignal();
        _release = NewSignal();
        Interlocked.Exchange(ref _armed, 1);
    }

    public Task WaitUntilReachedAsync(CancellationToken ct) => _reached.Task.WaitAsync(ct);

    public void Release() => _release.TrySetResult();

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("FROM refresh_tokens", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("\"TokenHash\"", StringComparison.Ordinal)
            && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
        {
            _reached.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        return result;
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// #468 — the grace window (#176) is measured against a timestamp the request
// captured itself. Both of these pin WHICH instant that may be, from opposite
// sides: a revocation stamped after this request started is the ordinary
// signature of concurrency and must still be graced, while one stamped after
// this request READ it is a clock anomaly and must fail inert — never as theft,
// which would revoke the very session the concurrent winner just won.
public sealed class RefreshGraceClockRaceTests(RefreshGraceClockRaceFactory factory)
    : IClassFixture<RefreshGraceClockRaceFactory>
{
    private static readonly WebApplicationFactoryClientOptions Cookieless =
        new() { HandleCookies = false };

    private Task<int> ActiveTokenCountAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, db =>
            db.RefreshTokens.CountAsync(t => t.AccountId == accountId && t.RevokedAt == null));

    // The deterministic form of the flake: the loser reads its clock, then its
    // token lookup is held open while the winner rotates the same token and
    // commits. The loser resumes, sees the revocation, and must take the grace
    // path — the winner's replacement is still the live tip, which is exactly
    // what grace exists for.
    //
    // Measuring the window from the pre-lookup instant makes `elapsed` NEGATIVE
    // here (the winner stamped RevokedAt from a clock read after the loser's),
    // which the skew guard read as theft: the whole family burned and the user
    // was signed out of every device by two tabs refreshing at once.
    [Fact]
    public async Task Refresh_LoserWhoseLookupResumesAfterTheWinnerCommitted_IsGraced_NotReadAsTheft()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateClient(Cookieless);

        var t0 = await factory.LoginAsync(email);

        factory.Barrier.Arm();
        var loser = client.PostRefreshAsync(t0.RefreshToken);
        // Parked before the lookup executes — so the loser has already captured
        // whatever instant it measures grace from, and has not yet observed the
        // rotation the winner is about to commit.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await factory.Barrier.WaitUntilReachedAsync(timeout.Token);

        var winner = await client.PostRefreshAsync(t0.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, winner.StatusCode);

        factory.Barrier.Release();
        var loserResponse = await loser;

        // 200 + one live tip is uniquely the grace path: theft would be 401 with
        // ZERO live (family revoked), and a true-overlap CAS loss would be 401
        // with one. Asserting both pins the branch, not just the outcome.
        Assert.Equal(HttpStatusCode.OK, loserResponse.StatusCode);
        Assert.Equal(1, await ActiveTokenCountAsync(accountId));
    }

    // The other side of the same boundary: a RevokedAt stamped ahead of the
    // instant this request READ it cannot be explained by concurrency — that
    // ordering is impossible once the window is measured from the read. It is a
    // clock anomaly (a node whose clock runs ahead, an NTP step), and evidence
    // about nothing. It must fail inert — a 401 like any losing tab — and must
    // NOT revoke the family: destroying a legitimate session is the one outcome
    // a clock disagreement must never cause.
    [Fact]
    public async Task Refresh_WhenTheRevocationIsStampedAheadOfThisRequestsClock_FailsInert_WithoutRevokingTheFamily()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateClient(Cookieless);

        var t0 = await factory.LoginAsync(email);
        var t1Response = await client.PostRefreshAsync(t0.RefreshToken); // t0 → t1, normally
        Assert.Equal(HttpStatusCode.OK, t1Response.StatusCode);
        var t1 = await TestHarness.ReadTokensAsync(t1Response);

        // Restamp t0's revocation into the future, as a node whose clock leads
        // this one would have written it. t0 is the only revoked row for this
        // freshly seeded account, so no token hash needs recomputing here.
        var ahead = DateTimeOffset.UtcNow.AddSeconds(5);
        await factory.WithTenantScopeAsync(accountId, async db =>
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE refresh_tokens SET "RevokedAt" = {ahead} WHERE "AccountId" = {accountId} AND "RevokedAt" IS NOT NULL"""));

        var replay = await client.PostRefreshAsync(t0.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // Inert, not theft: the live tip still refreshes and the family survives.
        Assert.Equal(HttpStatusCode.OK, (await client.PostRefreshAsync(t1.RefreshToken)).StatusCode);
        Assert.Equal(1, await ActiveTokenCountAsync(accountId));
    }
}
