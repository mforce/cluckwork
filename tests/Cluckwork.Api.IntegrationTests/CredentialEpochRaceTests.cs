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

public sealed class CredentialEpochRaceFactory : CluckworkWebApplicationFactory
{
    public EpochReplayBarrierInterceptor Barrier { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Jwt:RefreshReuseGraceSeconds", "0");
        builder.ConfigureTestServices(services =>
            services.AddDbContext<AppDbContext>((_, options) => options.AddInterceptors(Barrier)));
    }
}

// Pauses the replay-family UPDATE immediately before PostgreSQL executes it.
// By then RefreshAsync has loaded the user and accepted the presented epoch,
// which gives the test a deterministic window to bump the epoch and mint the
// replacement family before releasing the stale request.
public sealed class EpochReplayBarrierInterceptor : DbCommandInterceptor
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

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
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
        if (!command.CommandText.Contains("UPDATE refresh_tokens", StringComparison.OrdinalIgnoreCase)
            || !command.CommandText.Contains("RevokedAt", StringComparison.Ordinal)
            || Interlocked.CompareExchange(ref _armed, 0, 1) != 1)
            return;

        _reached.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class CredentialEpochRaceTests(CredentialEpochRaceFactory factory)
    : IClassFixture<CredentialEpochRaceFactory>
{
    [Fact]
    public async Task ActiveRefreshThatPassedTheOldEpochCheck_MintsAChildRejectedAfterTheBump()
    {
        var email = $"epoch-race-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var active = await factory.LoginAsync(email);

        factory.Barrier.Arm();
        var rotationTask = factory.CreateClient().PostRefreshAsync(active.RefreshToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await factory.Barrier.WaitUntilReachedAsync(timeout.Token);

        try
        {
            await factory.WithTenantScopeAsync(accountId, async db =>
            {
                var user = await db.Users.SingleAsync(candidate => candidate.Email == email);
                user.CredentialEpoch++;
                await db.SaveChangesAsync();
            });
        }
        finally
        {
            factory.Barrier.Release();
        }

        var rotation = await rotationTask;
        rotation.EnsureSuccessStatusCode();
        var child = await TestHarness.ReadTokensAsync(rotation);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().PostRefreshAsync(child.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task ReplayThatPassedTheOldEpochCheck_CannotRevokeTheFreshEpochFamily()
    {
        var email = $"epoch-race-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var stale = await factory.LoginAsync(email);
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var userId = await db.Users.Where(user => user.Email == email)
                .Select(user => user.Id).SingleAsync();
            var staleRow = await db.RefreshTokens.SingleAsync(token => token.UserId == userId);
            staleRow.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });

        factory.Barrier.Arm();
        var replayTask = factory.CreateClient().PostRefreshAsync(stale.RefreshToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await factory.Barrier.WaitUntilReachedAsync(timeout.Token);

        TokenPairDto fresh;
        try
        {
            await factory.WithTenantScopeAsync(accountId, async db =>
            {
                var user = await db.Users.SingleAsync(candidate => candidate.Email == email);
                user.CredentialEpoch++;
                await db.SaveChangesAsync();
            });
            fresh = await factory.LoginAsync(email);
        }
        finally
        {
            factory.Barrier.Release();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await replayTask).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await factory.CreateClient().PostRefreshAsync(fresh.RefreshToken)).StatusCode);
    }
}
