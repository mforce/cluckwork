namespace Cluckwork.Api.IntegrationTests;

using System.Data.Common;
using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

public sealed class CredentialEpochRaceFactory : CluckworkWebApplicationFactory
{
    public EpochReplayBarrierInterceptor Barrier { get; } = new();
    public CredentialResetBarrierInterceptor CredentialResetBarrier { get; } = new();
    public CountingPasswordHasher Hasher { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Jwt:RefreshReuseGraceSeconds", "0");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IPasswordHasher<ApplicationUser>>(Hasher);
            services.AddDbContext<AppDbContext>((_, options) =>
                options.AddInterceptors(Barrier, CredentialResetBarrier));
        });
    }
}

// Pauses the successful-password failed-count reset immediately before its
// UPDATE executes. The password has already been verified at this point. A
// concurrent password reset can then replace the credential epoch and security
// stamp, making Identity's UPDATE lose optimistic concurrency and reload the
// newer row into the request's tracked user.
public sealed class CredentialResetBarrierInterceptor : DbCommandInterceptor
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
        if (command.CommandText.Contains("UPDATE \"AspNetUsers\"", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("AccessFailedCount", StringComparison.Ordinal)
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
        var rotationTask = factory.CreateClient().PostRefreshAsync(active.RefreshToken, expectedAccount: accountId.ToString());
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
            (await factory.CreateClient().PostRefreshAsync(child.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
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
        var replayTask = factory.CreateClient().PostRefreshAsync(stale.RefreshToken, expectedAccount: accountId.ToString());
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
            (await factory.CreateClient().PostRefreshAsync(fresh.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
    }

    [Fact]
    public async Task LoginThatVerifiedTheOldPassword_CannotAdoptAConcurrentPasswordReset()
    {
        var email = $"login-proof-race-{Guid.NewGuid():N}@test.local";
        var rotatedPassword = TemporaryPassword.Generate();
        var accountId = await factory.SeedAccountWithUserAsync(email);
        await PrimeFailedCountAsync(email);

        factory.CredentialResetBarrier.Arm();
        var loginTask = factory.TryLoginAsync(email, TestHarness.Password);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await factory.CredentialResetBarrier.WaitUntilReachedAsync(timeout.Token);

        try
        {
            await RotatePasswordAsync(accountId, email, rotatedPassword);
        }
        finally
        {
            factory.CredentialResetBarrier.Release();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await loginTask).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await factory.TryLoginAsync(email, rotatedPassword)).StatusCode);
    }

    [Fact]
    public async Task StepUpThatVerifiedTheOldPassword_CannotAdoptAConcurrentPasswordReset()
    {
        var email = $"step-up-proof-race-{Guid.NewGuid():N}@test.local";
        var rotatedPassword = TemporaryPassword.Generate();
        var accountId = await factory.SeedAccountWithUserAsync(email);
        await PrimeFailedCountAsync(email);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users.Where(user => user.Email == email)
            .Select(user => user.Id).SingleAsync();
        var stepUp = scope.ServiceProvider.GetRequiredService<IStepUpGrantService>();

        factory.CredentialResetBarrier.Arm();
        var issueTask = stepUp.IssueAsync(accountId, userId, TestHarness.Password);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await factory.CredentialResetBarrier.WaitUntilReachedAsync(timeout.Token);

        try
        {
            await RotatePasswordAsync(accountId, email, rotatedPassword);
        }
        finally
        {
            factory.CredentialResetBarrier.Release();
        }

        var issued = await issueTask;
        Assert.True(issued.IsFailure);
        Assert.Equal("Users.CurrentPasswordIncorrect", issued.Error.Code);
    }

    [Theory]
    [InlineData("wrong-password")]
    [InlineData(TestHarness.Password)]
    public async Task DisabledLogin_PerformsOneFullHashWithoutMutatingLockout(string password)
    {
        var email = $"disabled-login-cost-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        await DisableAsync(accountId, email);
        factory.Hasher.Reset();

        var response = await factory.TryLoginAsync(email, password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, factory.Hasher.VerifyCount);
        await AssertLockoutUntouchedAsync(accountId, email);
    }

    [Theory]
    [InlineData("wrong-password")]
    [InlineData(TestHarness.Password)]
    public async Task DisabledStepUp_PerformsOneFullHashWithoutMutatingLockout(string password)
    {
        var email = $"disabled-step-up-cost-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        await DisableAsync(accountId, email);
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users.Where(user => user.Email == email)
            .Select(user => user.Id).SingleAsync();
        var stepUp = scope.ServiceProvider.GetRequiredService<IStepUpGrantService>();
        factory.Hasher.Reset();

        var result = await stepUp.IssueAsync(accountId, userId, password);

        Assert.True(result.IsFailure);
        Assert.Equal("Users.CurrentPasswordIncorrect", result.Error.Code);
        Assert.Equal(1, factory.Hasher.VerifyCount);
        await AssertLockoutUntouchedAsync(accountId, email);
    }

    private async Task PrimeFailedCountAsync(string email)
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.TryLoginAsync(email, "wrong-password")).StatusCode);
    }

    private Task RotatePasswordAsync(Guid accountId, string email, string rotatedPassword) =>
        factory.WithTenantScopeAsync(accountId, async db =>
        {
            var user = await db.Users.SingleAsync(candidate => candidate.Email == email);
            user.PasswordHash = new PasswordHasher<ApplicationUser>()
                .HashPassword(user, rotatedPassword);
            user.CredentialEpoch++;
            user.SecurityStamp = Guid.NewGuid().ToString();
            user.ConcurrencyStamp = Guid.NewGuid().ToString();
            await db.SaveChangesAsync();
        });

    private Task DisableAsync(Guid accountId, string email) =>
        factory.WithTenantScopeAsync(accountId, async db =>
        {
            var user = await db.Users.SingleAsync(candidate => candidate.Email == email);
            user.DisabledAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });

    private async Task AssertLockoutUntouchedAsync(Guid accountId, string email)
    {
        var state = await factory.WithTenantScopeAsync(accountId, async db => await db.Users
            .Where(user => user.Email == email)
            .Select(user => new { user.AccessFailedCount, user.LockoutEnd })
            .SingleAsync());
        Assert.Equal(0, state.AccessFailedCount);
        Assert.Null(state.LockoutEnd);
    }
}
