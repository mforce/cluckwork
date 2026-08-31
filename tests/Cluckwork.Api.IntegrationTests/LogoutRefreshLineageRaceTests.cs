namespace Cluckwork.Api.IntegrationTests;

using System.Data.Common;
using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

public sealed class LogoutRefreshLineageRaceFactory : CluckworkWebApplicationFactory
{
    public RefreshRotationBarrierInterceptor RefreshRotation { get; } = new();
    public LogoutTipUpdateBarrierInterceptor LogoutTipUpdate { get; } = new();
    public LogoutZeroRowRereadBarrierInterceptor LogoutZeroRowReread { get; } = new();
    public LogoutAncestorSeverBarrierInterceptor LogoutAncestorSever { get; } = new();
    public GraceInspectionBarrierInterceptor GraceInspection { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.AddDbContext<AppDbContext>((_, options) =>
                options.AddInterceptors(
                    RefreshRotation,
                    LogoutTipUpdate,
                    LogoutZeroRowReread,
                    LogoutAncestorSever,
                    GraceInspection)));
    }
}

// Parks the tracked refresh rotation after RefreshAsync has read the token but
// before its UPDATE + child INSERT batch executes. The child INSERT and the
// ReplacedByTokenHash assignment distinguish this command from every bulk
// revocation update. Disarming on the first match lets logout run unimpeded.
public sealed class RefreshRotationBarrierInterceptor : DbCommandInterceptor
{
    private TaskCompletionSource reached = NewSignal();
    private TaskCompletionSource release = NewSignal();
    private int armed;

    public int Hits { get; private set; }

    public void Arm()
    {
        reached = NewSignal();
        release = NewSignal();
        Hits = 0;
        Interlocked.Exchange(ref armed, 1);
    }

    public Task WaitUntilReachedAsync(CancellationToken ct) => reached.Task.WaitAsync(ct);

    public void Release() => release.TrySetResult();

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("UPDATE refresh_tokens", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("INSERT INTO refresh_tokens", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("\"ReplacedByTokenHash\"", StringComparison.Ordinal)
            && command.CommandText.Contains("\"ConcurrencyStamp\"", StringComparison.Ordinal)
            && Interlocked.CompareExchange(ref armed, 0, 1) == 1)
        {
            Hits++;
            reached.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }

        return result;
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// Parks logout's data-modifying CTE immediately before PostgreSQL executes it.
// The fenced_tip name and scalar SELECT distinguish the atomic tip CAS plus
// ancestor sever from refresh rotation and every family revoke. Disarming on
// one match lets any zero-row retry proceed.
public sealed class LogoutTipUpdateBarrierInterceptor : DbCommandInterceptor
{
    private TaskCompletionSource reached = NewSignal();
    private TaskCompletionSource release = NewSignal();
    private int armed;

    public int Hits { get; private set; }

    public void Arm()
    {
        reached = NewSignal();
        release = NewSignal();
        Hits = 0;
        Interlocked.Exchange(ref armed, 1);
    }

    public Task WaitUntilReachedAsync(CancellationToken ct) => reached.Task.WaitAsync(ct);

    public void Release() => release.TrySetResult();

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await WaitIfArmedAsync(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await WaitIfArmedAsync(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await WaitIfArmedAsync(command, cancellationToken);
        return result;
    }

    private async Task WaitIfArmedAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (command.CommandText.Contains("UPDATE refresh_tokens", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("SET \"RevokedAt\"", StringComparison.Ordinal)
            && command.CommandText.Contains("\"TokenHash\"", StringComparison.Ordinal)
            && command.CommandText.Contains("\"RevokedAt\" IS NULL", StringComparison.Ordinal)
            && !command.CommandText.Contains("INSERT INTO refresh_tokens", StringComparison.OrdinalIgnoreCase)
            && Interlocked.CompareExchange(ref armed, 0, 1) == 1)
        {
            Hits++;
            reached.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// Armed only after a competing refresh has committed while logout's tip fence
// was parked. The next root-scoped lineage read is therefore the mandatory
// zero-result reread, never an initial node read.
public sealed class LogoutZeroRowRereadBarrierInterceptor : DbCommandInterceptor
{
    private TaskCompletionSource reached = NewSignal();
    private TaskCompletionSource release = NewSignal();
    private int armed;

    public int Hits { get; private set; }

    public void Arm()
    {
        reached = NewSignal();
        release = NewSignal();
        Hits = 0;
        Interlocked.Exchange(ref armed, 1);
    }

    public Task WaitUntilReachedAsync(CancellationToken ct) => reached.Task.WaitAsync(ct);

    public void Release() => release.TrySetResult();

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (IsRootScopedLineageRead(command)
            && Interlocked.CompareExchange(ref armed, 0, 1) == 1)
        {
            Hits++;
            reached.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }

        return result;
    }

    private static bool IsRootScopedLineageRead(DbCommand command) =>
        command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
        && command.CommandText.Contains("refresh_tokens", StringComparison.OrdinalIgnoreCase)
        && command.CommandText.Contains("\"TokenHash\"", StringComparison.Ordinal)
        && command.CommandText.Contains("\"UserId\"", StringComparison.Ordinal)
        && command.CommandText.Contains("\"AccountId\"", StringComparison.Ordinal)
        && command.CommandText.Contains("\"IssuedEpoch\"", StringComparison.Ordinal)
        && command.CommandText.Contains("\"ReplacedByTokenHash\"", StringComparison.Ordinal);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// A correct implementation has no separately observable ancestor-sever
// command. This barrier stays untouched in production and gives the required
// split-statement mutation a deterministic fence/sever gap.
public sealed class LogoutAncestorSeverBarrierInterceptor : DbCommandInterceptor
{
    private TaskCompletionSource reached = NewSignal();
    private TaskCompletionSource release = NewSignal();
    private int armed;

    public int Hits { get; private set; }

    public void Arm()
    {
        reached = NewSignal();
        release = NewSignal();
        Hits = 0;
        Interlocked.Exchange(ref armed, 1);
    }

    public Task WaitUntilReachedAsync(CancellationToken ct) => reached.Task.WaitAsync(ct);

    public void Release() => release.TrySetResult();

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("UPDATE refresh_tokens", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("SET \"ReplacedByTokenHash\" = NULL", StringComparison.Ordinal)
            && !command.CommandText.Contains("fenced_tip", StringComparison.Ordinal)
            && Interlocked.CompareExchange(ref armed, 0, 1) == 1)
        {
            Hits++;
            reached.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }

        return result;
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// Parks grace inspection after RefreshAsync's original parent lookup. The old
// split implementation reaches this barrier on its later child lookup; the
// coherent implementation reaches it before its one fresh parent/child read.
public sealed class GraceInspectionBarrierInterceptor : DbCommandInterceptor
{
    private TaskCompletionSource reached = NewSignal();
    private TaskCompletionSource release = NewSignal();
    private int armed;

    public int Hits { get; private set; }

    public void Arm()
    {
        reached = NewSignal();
        release = NewSignal();
        Hits = 0;
        Interlocked.Exchange(ref armed, 1);
    }

    public Task WaitUntilReachedAsync(CancellationToken ct) => reached.Task.WaitAsync(ct);

    public void Release() => release.TrySetResult();

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        var whereIndex = command.CommandText.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
        var predicate = whereIndex < 0 ? string.Empty : command.CommandText[whereIndex..];
        if (command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("refresh_tokens", StringComparison.OrdinalIgnoreCase)
            && predicate.Contains("\"TokenHash\"", StringComparison.Ordinal)
            && predicate.Contains("\"UserId\"", StringComparison.Ordinal)
            && predicate.Contains("\"AccountId\"", StringComparison.Ordinal)
            && command.CommandText.Contains("\"ReplacedByTokenHash\"", StringComparison.Ordinal)
            && Interlocked.CompareExchange(ref armed, 0, 1) == 1)
        {
            Hits++;
            reached.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }

        return result;
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class LogoutRefreshLineageRaceTests(LogoutRefreshLineageRaceFactory factory)
    : IClassFixture<LogoutRefreshLineageRaceFactory>
{
    private static readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        Cookieless = new() { HandleCookies = false };

    [Fact]
    public async Task LogoutWins_FencesTheLoadedRefreshAndPreservesSiblingAndForeignSessions()
    {
        var email = $"logout-wins-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var presented = await factory.LoginAsync(email);
        var sibling = await factory.LoginAsync(email);
        var foreignEmail = $"logout-wins-foreign-{Guid.NewGuid():N}@test.local";
        var foreignAccountId = await factory.SeedAccountWithUserAsync(foreignEmail);
        var foreign = await factory.LoginAsync(foreignEmail);
        var client = factory.CreateClient(Cookieless);

        factory.RefreshRotation.Arm();
        var refreshTask = client.PostRefreshAsync(
            presented.RefreshToken, expectedAccount: accountId.ToString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await factory.RefreshRotation.WaitUntilReachedAsync(timeout.Token);

        HttpResponseMessage logout;
        try
        {
            logout = await client.PostLogoutAsync(presented.RefreshToken, accountId: accountId);
        }
        finally
        {
            factory.RefreshRotation.Release();
        }

        var refresh = await refreshTask;
        Assert.Equal(1, factory.RefreshRotation.Hits);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        var (total, live) = await TokenCountsAsync(accountId);
        Assert.Equal(2, total); // the losing refresh's child INSERT rolled back
        Assert.Equal(1, live);  // only the sibling remains
        Assert.Equal(HttpStatusCode.OK, (await client.PostRefreshAsync(
            sibling.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostRefreshAsync(
            foreign.RefreshToken, expectedAccount: foreignAccountId.ToString())).StatusCode);
    }

    [Fact]
    public async Task RefreshWins_LogoutFollowsAndRevokesTheDeliveredChildWithoutTouchingOtherSessions()
    {
        var email = $"refresh-wins-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var presented = await factory.LoginAsync(email);
        var sibling = await factory.LoginAsync(email);
        var foreignEmail = $"refresh-wins-foreign-{Guid.NewGuid():N}@test.local";
        var foreignAccountId = await factory.SeedAccountWithUserAsync(foreignEmail);
        var foreign = await factory.LoginAsync(foreignEmail);
        var client = factory.CreateClient(Cookieless);

        factory.LogoutTipUpdate.Arm();
        var logoutTask = client.PostLogoutAsync(presented.RefreshToken, accountId: accountId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await factory.LogoutTipUpdate.WaitUntilReachedAsync(timeout.Token);

        HttpResponseMessage refresh;
        try
        {
            refresh = await client.PostRefreshAsync(
                presented.RefreshToken, expectedAccount: accountId.ToString());
        }
        finally
        {
            factory.LogoutTipUpdate.Release();
        }

        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var deliveredChild = await TestHarness.ReadTokensAsync(refresh);
        var logout = await logoutTask;

        Assert.Equal(1, factory.LogoutTipUpdate.Hits);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        // Prove logout itself remained on the selected lineage before presenting
        // the now-severed tip, whose later reuse intentionally takes #176's
        // strict family-replay path.
        Assert.Equal(HttpStatusCode.OK, (await client.PostRefreshAsync(
            sibling.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostRefreshAsync(
            foreign.RefreshToken, expectedAccount: foreignAccountId.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostRefreshAsync(
            deliveredChild.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
    }

    [Fact]
    public async Task RefreshWins_AncestorSeverWaitsForReturnedTipFence()
    {
        var email = $"returned-tip-gate-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var parent = await factory.LoginAsync(email);
        var client = factory.CreateClient(Cookieless);
        var childResponse = await client.PostRefreshAsync(
            parent.RefreshToken, expectedAccount: accountId.ToString());
        childResponse.EnsureSuccessStatusCode();
        var child = await TestHarness.ReadTokensAsync(childResponse);
        var parentId = await ParentIdAsync(accountId);

        factory.LogoutTipUpdate.Arm();
        var logoutTask = client.PostLogoutAsync(parent.RefreshToken, accountId: accountId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await factory.LogoutTipUpdate.WaitUntilReachedAsync(timeout.Token);

        var refresh = await client.PostRefreshAsync(
            child.RefreshToken, expectedAccount: accountId.ToString());
        refresh.EnsureSuccessStatusCode();

        factory.LogoutZeroRowReread.Arm();
        factory.LogoutTipUpdate.Release();
        await factory.LogoutZeroRowReread.WaitUntilReachedAsync(timeout.Token);

        try
        {
            var pointer = await ReplacementPointerAsync(accountId, parentId);
            Assert.NotNull(pointer);
        }
        finally
        {
            factory.LogoutZeroRowReread.Release();
        }

        Assert.Equal(1, factory.LogoutTipUpdate.Hits);
        Assert.Equal(1, factory.LogoutZeroRowReread.Hits);
        Assert.Equal(HttpStatusCode.NoContent, (await logoutTask).StatusCode);
    }

    [Fact]
    public async Task StaleParentRefresh_CannotObserveFencedChildBeforePointerSever()
    {
        var email = $"stale-parent-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var parent = await factory.LoginAsync(email);
        var sibling = await factory.LoginAsync(email);
        var foreignEmail = $"stale-parent-foreign-{Guid.NewGuid():N}@test.local";
        var foreignAccountId = await factory.SeedAccountWithUserAsync(foreignEmail);
        var foreign = await factory.LoginAsync(foreignEmail);
        var client = factory.CreateClient(Cookieless);
        var childResponse = await client.PostRefreshAsync(
            parent.RefreshToken, expectedAccount: accountId.ToString());
        childResponse.EnsureSuccessStatusCode();

        factory.GraceInspection.Arm();
        factory.LogoutAncestorSever.Arm();
        var staleRefreshTask = client.PostRefreshAsync(
            parent.RefreshToken, expectedAccount: accountId.ToString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await factory.GraceInspection.WaitUntilReachedAsync(timeout.Token);

        var logoutTask = client.PostLogoutAsync(parent.RefreshToken, accountId: accountId);
        var separateSeverTask = factory.LogoutAncestorSever.WaitUntilReachedAsync(timeout.Token);
        try
        {
            var first = await Task.WhenAny(logoutTask, separateSeverTask);
            if (first == separateSeverTask)
            {
                await separateSeverTask;
                factory.GraceInspection.Release();
                Assert.Equal(HttpStatusCode.Unauthorized, (await staleRefreshTask).StatusCode);
            }
            else
            {
                Assert.Equal(HttpStatusCode.NoContent, (await logoutTask).StatusCode);
                factory.GraceInspection.Release();
            }
        }
        finally
        {
            factory.GraceInspection.Release();
            factory.LogoutAncestorSever.Release();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await logoutTask).StatusCode);
        Assert.Equal(1, factory.GraceInspection.Hits);
        Assert.Equal(HttpStatusCode.Unauthorized, (await staleRefreshTask).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostRefreshAsync(
            sibling.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostRefreshAsync(
            foreign.RefreshToken, expectedAccount: foreignAccountId.ToString())).StatusCode);
        Assert.Equal(0, factory.LogoutAncestorSever.Hits);
    }

    private Task<Guid> ParentIdAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, db => db.RefreshTokens.AsNoTracking()
            .Where(token => token.AccountId == accountId && token.ReplacedByTokenHash != null)
            .Select(token => token.Id)
            .SingleAsync());

    private Task<string?> ReplacementPointerAsync(Guid accountId, Guid tokenId) =>
        factory.WithTenantScopeAsync(accountId, async db =>
        {
            var row = await db.RefreshTokens.AsNoTracking()
                .Where(token => token.AccountId == accountId && token.Id == tokenId)
                .Select(token => new { token.ReplacedByTokenHash })
                .SingleAsync();
            return row.ReplacedByTokenHash;
        });

    private Task<(int Total, int Live)> TokenCountsAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, async db =>
        {
            var tokens = await db.RefreshTokens.AsNoTracking()
                .Where(token => token.AccountId == accountId)
                .Select(token => token.RevokedAt)
                .ToListAsync();
            return (tokens.Count, tokens.Count(revokedAt => revokedAt is null));
        });
}
