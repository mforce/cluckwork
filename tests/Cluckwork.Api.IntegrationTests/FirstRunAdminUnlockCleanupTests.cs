namespace Cluckwork.Api.IntegrationTests;

using System.Data.Common;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

// PR #339 review — a session-scoped pg_advisory_unlock that fails AFTER
// FirstRunAdminService.ProvisionUnderLockAsync already committed the first
// Owner must not discard that successful Result: the generated temporary
// password is printed exactly once and stored nowhere else (only its hash is
// persisted), so losing the Result strands the operator behind break-glass
// recovery even though the Owner row exists and a retry now takes the
// idempotent no-op branch. AdvisoryLockFaultInterceptor is a
// DbCommandInterceptor that throws on-demand for one of two very specific,
// deterministic command shapes — never by racing a real dropped connection —
// mirroring the SqlCaptureInterceptor/DbCommandInterceptor fault-injection
// technique already used against this AppDbContext in
// ReportQueryBoundingTests (fix/311-bounded-reports).
//
// Each test below gets its OWN factory instance (own throwaway Postgres
// container, own migration) rather than sharing one via IClassFixture across
// multiple [Fact]s: FirstRunAdminService provisions the single fixed
// SeedDefaults.AccountId, so two tests sharing a database could never both
// observe "no Owner yet".
public sealed class FirstRunAdminUnlockCleanupFactory : CluckworkWebApplicationFactory
{
    public AdvisoryLockFaultInterceptor Interceptor { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.AddDbContext<AppDbContext>((_, options) => options.AddInterceptors(Interceptor)));
    }
}

public sealed class AdvisoryLockFaultInterceptor : DbCommandInterceptor
{
    // Toggled by a test before calling ProvisionAsync; simulates the finding's
    // scenario ("the database connection drops immediately after the
    // commit") by making the CLIENT-side unlock call itself fail — the
    // deterministic substitute the task calls for, instead of racing a real
    // dropped container connection.
    public volatile bool FailOnAdvisoryUnlock;

    // One-shot: armed by a test wanting a GENUINE failure from inside
    // ProvisionUnderLockAsync (the regression check — cleanup suppression
    // must not have widened into swallowing a real provisioning error). Fires
    // on the very first AspNetUsers query ProvisionUnderLockAsync issues
    // (GetUsersInRoleAsync) and then disarms itself.
    private int _failNextUsersQuery;

    public void ArmFailNextUsersQuery() => Interlocked.Exchange(ref _failNextUsersQuery, 1);

    private void MaybeFail(DbCommand command)
    {
        if (FailOnAdvisoryUnlock &&
            command.CommandText.Contains("pg_advisory_unlock", StringComparison.Ordinal))
            throw new SimulatedCleanupFaultException();

        if (command.CommandText.Contains("\"AspNetUsers\"", StringComparison.Ordinal) &&
            Interlocked.CompareExchange(ref _failNextUsersQuery, 0, 1) == 1)
            throw new SimulatedProvisioningFaultException();
    }

    // ExecuteSqlInterpolatedAsync dispatches through NonQueryExecutingAsync
    // regardless of the SQL's shape (it ignores any result set); ordinary
    // LINQ queries (GetUsersInRoleAsync, the conflicting-email lookup) go
    // through ReaderExecutingAsync. Both are covered so the fault fires
    // wherever EF actually sends the targeted command, without depending on
    // which internal path a given call takes.
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        MaybeFail(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        MaybeFail(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

// Distinguishable from any real failure so assertions can tell "our injected
// fault" apart from an unrelated bug surfacing as a generic Exception.
public sealed class SimulatedCleanupFaultException()
    : Exception("Simulated pg_advisory_unlock failure (test fault injection).");

public sealed class SimulatedProvisioningFaultException()
    : Exception("Simulated AspNetUsers query failure (test fault injection).");

public sealed class UnlockFailureAfterCommitTests : IClassFixture<FirstRunAdminUnlockCleanupFactory>
{
    private readonly FirstRunAdminUnlockCleanupFactory _factory;

    public UnlockFailureAfterCommitTests(FirstRunAdminUnlockCleanupFactory factory)
    {
        _factory = factory;
        _ = _factory.Services; // force host + migration before the fault is armed
    }

    // The finding, reproduced deterministically: the unlock throws AFTER
    // ProvisionUnderLockAsync's commit succeeded. The Result must still be
    // Success, still carry the one-time temporary password, and the Owner
    // row must exist — none of that may be discarded by cleanup failing.
    [Fact]
    public async Task UnlockFailure_AfterSuccessfulCommit_StillReturnsSuccessWithPassword_AndOwnerExists()
    {
        _factory.Interceptor.FailOnAdvisoryUnlock = true;
        var email = $"unlock-fail-{Guid.NewGuid():N}@test.local";

        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<FirstRunAdminService>();

        var result = await service.ProvisionAsync(email);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Description : null);
        Assert.False(result.Value.WasAlreadyProvisioned);
        Assert.Equal(email, result.Value.Email);
        Assert.Equal(SeedDefaults.AccountId, result.Value.AccountId);
        Assert.False(string.IsNullOrEmpty(result.Value.TemporaryPassword));

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == email);
        Assert.Equal(SeedDefaults.AccountId, user.AccountId);
        Assert.True(await users.IsInRoleAsync(user, Roles.Owner));
    }
}

public sealed class GenuineProvisioningFailureStillFailsTests : IClassFixture<FirstRunAdminUnlockCleanupFactory>
{
    private readonly FirstRunAdminUnlockCleanupFactory _factory;

    public GenuineProvisioningFailureStillFailsTests(FirstRunAdminUnlockCleanupFactory factory)
    {
        _factory = factory;
        _ = _factory.Services;
    }

    // Regression guard: only cleanup (the unlock / connection close) may be
    // suppressed. A genuine failure from INSIDE ProvisionUnderLockAsync
    // itself must still propagate exactly as before — cleanup-failure
    // tolerance must not have widened into swallowing real errors.
    [Fact]
    public async Task GenuineFailureInsideTheCriticalSection_StillPropagates_AndCreatesNoOwner()
    {
        _factory.Interceptor.ArmFailNextUsersQuery();
        var email = $"genuine-fail-{Guid.NewGuid():N}@test.local";

        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<FirstRunAdminService>();

        await Assert.ThrowsAsync<SimulatedProvisioningFaultException>(
            () => service.ProvisionAsync(email));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var anyOwner = await db.Users.IgnoreQueryFilters()
            .AnyAsync(u => u.AccountId == SeedDefaults.AccountId);
        Assert.False(anyOwner, "a genuine mid-provisioning failure must not leave a committed Owner behind");
    }
}

public sealed class HappyPathStillReleasesTheLockTests : IClassFixture<FirstRunAdminUnlockCleanupFactory>
{
    private readonly FirstRunAdminUnlockCleanupFactory _factory;

    public HappyPathStillReleasesTheLockTests(FirstRunAdminUnlockCleanupFactory factory)
    {
        _factory = factory;
        _ = _factory.Services;
    }

    // No fault armed: confirms the ordinary path is untouched by the
    // try/catch added around cleanup — password still returned, and the
    // session-scoped advisory lock is actually gone from Postgres afterward
    // (checked directly via pg_locks on an independent connection, not
    // inferred from a lack of a hang).
    [Fact]
    public async Task HappyPath_ReturnsPassword_AndActuallyReleasesTheAdvisoryLock()
    {
        var email = $"happy-{Guid.NewGuid():N}@test.local";

        using (var scope = _factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<FirstRunAdminService>();
            var result = await service.ProvisionAsync(email);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Description : null);
            Assert.False(result.Value.WasAlreadyProvisioned);
            Assert.False(string.IsNullOrEmpty(result.Value.TemporaryPassword));
        }

        await using var probe = new NpgsqlConnection(_factory.ConnectionString);
        await probe.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND classid = 283 AND objid = 1",
            probe);
        var held = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(0, held);
    }
}
