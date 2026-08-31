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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.AddDbContext<AppDbContext>((_, options) =>
                options.AddInterceptors(RefreshRotation, LogoutTipUpdate)));
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

// Parks logout's single-row conditional tip UPDATE immediately before
// PostgreSQL executes it. TokenHash + RevokedAt distinguish the tip CAS from a
// user/epoch family revoke; absence of the child INSERT distinguishes it from
// refresh rotation. Disarming on one match lets any zero-row retry proceed.
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
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostRefreshAsync(
            deliveredChild.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostRefreshAsync(
            sibling.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostRefreshAsync(
            foreign.RefreshToken, expectedAccount: foreignAccountId.ToString())).StatusCode);
    }

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
