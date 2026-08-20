namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #265 — break-glass recovery fixture: seeds a known admin into its OWN
// Postgres container so resetting that admin's password (which the tests do)
// can't disturb any other suite. Own container per the #279 isolation rule: a
// suite that mutates a full seeded fixture must not share a database.
//
// #283 — the default account/Admin role are now migration-baked static
// reference data (no Seed:* config); the admin USER is seeded directly here
// (via InitializeAsync, so it's in place before any [Fact] runs — mirrors
// what a real `bootstrap-admin` run would produce), standing in for that
// command exactly like SeedAndFlockTests/BaselineSeedCurrencyTests do.
public sealed class BreakGlassRecoveryFixture : CluckworkWebApplicationFactory, IAsyncLifetime
{
    // Runtime-generated — never a hardcoded credential (GitGuardian scans PRs).
    public string AdminEmail { get; } = $"breakglass-{Guid.NewGuid():N}@test.local";
    public string AdminPassword { get; } = TestHarness.Password;

    // NOTE: redeclaring `IAsyncLifetime` (both here and in the base list
    // above) is required for xUnit to dispatch to THIS override —
    // CluckworkWebApplicationFactory.InitializeAsync() is not virtual, so a
    // `new` method alone is silently skipped by any code that calls it
    // through an IAsyncLifetime reference, as xUnit does (mirrors the same
    // pattern in SimulationSeederTests).
    public new async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await this.SeedUserAsync(SeedDefaults.AccountId, AdminEmail, Roles.Owner);
    }
}

public sealed class AdminRecoveryServiceTests : IClassFixture<BreakGlassRecoveryFixture>
{
    private readonly BreakGlassRecoveryFixture _factory;

    public AdminRecoveryServiceTests(BreakGlassRecoveryFixture factory)
    {
        _factory = factory;
        _ = _factory.Services; // force host startup (IAsyncLifetime.InitializeAsync seeds the admin)
    }

    private IServiceScope Scope() => _factory.Services.CreateScope();

    [Fact]
    public async Task Recover_ResetsPassword_RevokesSessions_RotatesStamp_AndAuditsBreakGlass()
    {
        // A live session (refresh token) + the pre-reset security stamp, which
        // recovery must evict / rotate. Fresh scope per step so no step reads a
        // stale identity-map copy of the next step's writes.
        string oldRefreshToken;
        Guid adminUserId;
        Guid adminAccountId;
        string originalStamp;
        int originalCredentialEpoch;
        using (var s = Scope())
        {
            var idp = s.ServiceProvider.GetRequiredService<IIdentityProvider>();
            var login = await idp.LoginAsync(SeedDefaults.AccountId, _factory.AdminEmail, _factory.AdminPassword);
            Assert.True(login.IsSuccess, "seeded admin should log in before recovery: " + (login.IsFailure ? login.Error.Code + " " + login.Error.Description : ""));
            oldRefreshToken = login.Value.RefreshToken;

            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var admin = await db.Users.SingleAsync(u => u.Email == _factory.AdminEmail);
            adminUserId = admin.Id;
            adminAccountId = admin.AccountId;
            originalStamp = admin.SecurityStamp!;
            originalCredentialEpoch = admin.CredentialEpoch;

            // Lock the account out — the exact state break-glass must recover from
            // (repeated failed logins → a 15-min lockout). LoginAsync checks
            // IsLockedOutAsync BEFORE the password, so the temporary password can
            // only succeed below if the reset actually cleared the lockout (#265
            // review).
            var um = s.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await um.SetLockoutEndDateAsync(admin, DateTimeOffset.UtcNow.AddMinutes(30));
            Assert.True(await um.IsLockedOutAsync(admin), "precondition: admin should be locked out");
        }

        // Break-glass reset — recover using an UPPERCASE spelling of the email to
        // prove the lookup matches NormalizedEmail (case-insensitive), like login.
        string tempPassword;
        using (var s = Scope())
        {
            var svc = s.ServiceProvider.GetRequiredService<AdminRecoveryService>();
            var result = await svc.RecoverAsync(
                _factory.AdminEmail.ToUpperInvariant(), accountId: null, reason: "quarterly drill");
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Description : "");
            Assert.Equal(_factory.AdminEmail, result.Value.Email);
            tempPassword = result.Value.TemporaryPassword;
        }

        // The temporary password logs in (proving the lockout was cleared); the
        // old one no longer does.
        using (var s = Scope())
        {
            var idp = s.ServiceProvider.GetRequiredService<IIdentityProvider>();
            Assert.True((await idp.LoginAsync(SeedDefaults.AccountId, _factory.AdminEmail, tempPassword)).IsSuccess,
                "temporary password should log in (the reset must clear the lockout)");
            Assert.True((await idp.LoginAsync(SeedDefaults.AccountId, _factory.AdminEmail, _factory.AdminPassword)).IsFailure,
                "old password must be rejected after recovery");

            var um = s.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var admin = await um.FindByEmailAsync(_factory.AdminEmail);
            Assert.False(await um.IsLockedOutAsync(admin!), "the lockout must be cleared by recovery");
        }

        // The refresh token minted before recovery is dead.
        using (var s = Scope())
        {
            var idp = s.ServiceProvider.GetRequiredService<IIdentityProvider>();
            // The UnescapeDataString is a NO-OP on purpose: this token never
            // came from a cookie — it is the raw string straight out of
            // LoginAsync's TokenPair record, not a percent-encoded Set-Cookie
            // value like RetryBoundaryTests captures. No decode needed; kept
            // so this call site reads identically to the cookie-sourced ones
            // and nobody "fixes" it by removing a decode that was never there.
            Assert.True((await idp.RefreshAsync(oldRefreshToken)).IsFailure,
                "the refresh token minted before recovery must be revoked");
        }

        // The security stamp rotated, and a conspicuous break-glass audit row exists.
        using (var s = Scope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var admin = await db.Users.SingleAsync(u => u.Id == adminUserId);
            Assert.NotEqual(originalStamp, admin.SecurityStamp);
            Assert.Equal(originalCredentialEpoch + 1, admin.CredentialEpoch);

            // AuditEvent carries the tenant query filter, so IgnoreQueryFilters is
            // required here — this scope never resolved a tenant.
            var row = await db.AuditEvents.IgnoreQueryFilters().SingleOrDefaultAsync(a =>
                a.Action == "User.BreakGlassReset" && a.EntityId == adminUserId
                && a.AccountId == adminAccountId && a.Reason == "quarterly drill");
            Assert.True(row is not null,
                "expected a User.BreakGlassReset audit row stamped to the admin's account, carrying the reason");

            // #500 — this row used to read "(unresolved)": a fallback nobody
            // chose. The verb has no signed-in human by design, so it now
            // declares WHICH non-person it is.
            Assert.Equal(SystemActors.BreakGlass, row!.ActorEmail);
            Assert.Equal(Guid.Empty, row.ActorUserId);

            // The label replaces the placeholder; it does NOT replace the real
            // accountability, which is the host + OS user in the details blob.
            // Asserted together so "fixed the actor, dropped the context" cannot
            // pass.
            Assert.Contains(Environment.MachineName, row.DetailsJson);
            Assert.Contains(Environment.UserName, row.DetailsJson);
        }
    }

    [Fact]
    public async Task Recover_UnknownEmail_ReturnsNotFound()
    {
        using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<AdminRecoveryService>();
        var result = await svc.RecoverAsync($"nobody-{Guid.NewGuid():N}@test.local", accountId: null, reason: null);
        Assert.True(result.IsFailure);
        Assert.Equal("Recovery.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task Recover_BlankEmail_ReturnsValidationError()
    {
        using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<AdminRecoveryService>();
        var result = await svc.RecoverAsync("   ", accountId: null, reason: null);
        Assert.True(result.IsFailure);
        Assert.Equal("Recovery.EmailRequired", result.Error.Code);
    }

    [Fact]
    public async Task Recover_RaceWithAConcurrentDisable_NeverReportsSuccessForADisabledUser()
    {
        // #492 round 3 (codex) — the upfront DisabledAt check in RecoverAsync
        // is a fast, UNLOCKED read. Without a re-check serialized against the
        // disable path's own account lock, a disable landing between that read
        // and BreakGlassResetAsync actually running would let recovery reset
        // the password, write the audit row, print a credential that cannot
        // work, and exit 0 anyway — the identical false-green Recovery.
        // UserDisabled exists to prevent, reached one race window later.
        //
        // Constructed deterministically with the same account-row fence
        // DisableUserRaceTests uses, not by timing luck: park RecoverAsync on
        // the lock AFTER its upfront check has already passed (the target is
        // still active at that point), disable the target through a SEPARATE
        // connection while it's queued, then release. A correct recovery must
        // now refuse; a naive one — upfront check only — would still succeed.
        var email = $"race-{Guid.NewGuid():N}@test.local";
        var accountId = await _factory.SeedAccountWithUserAsync(email);
        Guid userId;
        using (var setup = Scope())
        {
            var identity = setup.ServiceProvider.GetRequiredService<IIdentityProvider>();
            setup.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
            userId = (await identity.ListUsersAsync(accountId)).Single(u => u.Email == email).Id;
        }

        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        await using var fenceDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_factory.ConnectionString).Options, tenant);
        await using var fenceTx = await fenceDb.Database.BeginTransactionAsync();
        await fenceDb.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""");
        var fencePid = await fenceDb.BackendPidAsync();

        var recover = Task.Run(async () =>
        {
            using var scope = Scope();
            var svc = scope.ServiceProvider.GetRequiredService<AdminRecoveryService>();
            return await svc.RecoverAsync(email, accountId, reason: "race probe");
        });
        Assert.True(await _factory.WaitUntilDoneOrBlockedAsync(recover, fencePid),
            "recovery must park on the account lock after its upfront check, not sail past it");

        // Disabled through a SEPARATE connection while recovery is queued — the
        // upfront check already ran and saw an ACTIVE user.
        await using (var disablingDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_factory.ConnectionString).Options, tenant))
        {
            var user = await disablingDb.Users.SingleAsync(u => u.Id == userId);
            user.DisabledAt = DateTimeOffset.UtcNow;
            await disablingDb.SaveChangesAsync();
        }

        await fenceTx.CommitAsync(); // releases the lock; the disable is already durable

        var result = await recover;

        Assert.True(result.IsFailure, "recovery must not report success for a user disabled while it was queued");
        Assert.Equal("Recovery.UserDisabled", result.Error.Code);

        await using var verifyDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_factory.ConnectionString).Options, tenant);
        var after = await verifyDb.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        Assert.False(
            await verifyDb.AuditEvents.IgnoreQueryFilters()
                .AnyAsync(a => a.Action == "User.BreakGlassReset" && a.EntityId == userId),
            "a recovery that lost this race must not leave an audit row claiming it happened");
    }

    [Fact]
    public async Task Recover_DisabledUser_FailsLoudly_AndChangesNothing()
    {
        // #356 — without this refusal, break-glass becomes a SILENT FALSE GREEN
        // against a disabled user: LoginAsync rejects them before the password
        // is ever checked, so the command would print a working-looking
        // credential, write a User.BreakGlassReset audit row and exit 0 for an
        // account that is still locked out — in the one emergency the tool
        // exists for. #265 requires it to be fail-loud, never a silent no-op.
        var email = $"disabled-{Guid.NewGuid():N}@test.local";
        await _factory.SeedUserAsync(SeedDefaults.AccountId, email, Roles.Owner);

        Guid userId;
        string hashBefore;
        string stampBefore;
        using (var s = Scope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == email);
            user.DisabledAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            userId = user.Id;
            hashBefore = user.PasswordHash!;
            stampBefore = user.SecurityStamp!;
        }

        using (var s = Scope())
        {
            var svc = s.ServiceProvider.GetRequiredService<AdminRecoveryService>();
            var result = await svc.RecoverAsync(email, accountId: null, reason: "drill");

            Assert.True(result.IsFailure, "recovering a disabled user must NOT report success");
            Assert.Equal("Recovery.UserDisabled", result.Error.Code);
        }

        // "Changes nothing" is the other half: a refusal that had already reset
        // the password would still have burned the credential it refused to fix.
        using (var s = Scope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var after = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
            Assert.Equal(hashBefore, after.PasswordHash);
            Assert.Equal(stampBefore, after.SecurityStamp);
            Assert.NotNull(after.DisabledAt);
            Assert.False(
                await db.AuditEvents.IgnoreQueryFilters()
                    .AnyAsync(a => a.Action == "User.BreakGlassReset" && a.EntityId == userId),
                "a refused recovery must not leave a break-glass audit row claiming it happened");
        }
    }
}
