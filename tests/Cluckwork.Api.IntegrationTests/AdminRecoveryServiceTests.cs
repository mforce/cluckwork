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
        using (var s = Scope())
        {
            var idp = s.ServiceProvider.GetRequiredService<IIdentityProvider>();
            var login = await idp.LoginAsync(_factory.AdminEmail, _factory.AdminPassword);
            Assert.True(login.IsSuccess, "seeded admin should log in before recovery: " + (login.IsFailure ? login.Error.Code + " " + login.Error.Description : ""));
            oldRefreshToken = login.Value.RefreshToken;

            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var admin = await db.Users.SingleAsync(u => u.Email == _factory.AdminEmail);
            adminUserId = admin.Id;
            adminAccountId = admin.AccountId;
            originalStamp = admin.SecurityStamp!;

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
            Assert.True((await idp.LoginAsync(_factory.AdminEmail, tempPassword)).IsSuccess,
                "temporary password should log in (the reset must clear the lockout)");
            Assert.True((await idp.LoginAsync(_factory.AdminEmail, _factory.AdminPassword)).IsFailure,
                "old password must be rejected after recovery");

            var um = s.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var admin = await um.FindByEmailAsync(_factory.AdminEmail);
            Assert.False(await um.IsLockedOutAsync(admin!), "the lockout must be cleared by recovery");
        }

        // The refresh token minted before recovery is dead.
        using (var s = Scope())
        {
            var idp = s.ServiceProvider.GetRequiredService<IIdentityProvider>();
            Assert.True((await idp.RefreshAsync(oldRefreshToken)).IsFailure,
                "the refresh token minted before recovery must be revoked");
        }

        // The security stamp rotated, and a conspicuous break-glass audit row exists.
        using (var s = Scope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var admin = await db.Users.SingleAsync(u => u.Id == adminUserId);
            Assert.NotEqual(originalStamp, admin.SecurityStamp);

            // AuditEvent carries the tenant query filter, so IgnoreQueryFilters is
            // required here — this scope never resolved a tenant.
            var audited = await db.AuditEvents.IgnoreQueryFilters().AnyAsync(a =>
                a.Action == "User.BreakGlassReset" && a.EntityId == adminUserId
                && a.AccountId == adminAccountId && a.Reason == "quarterly drill");
            Assert.True(audited,
                "expected a User.BreakGlassReset audit row stamped to the admin's account, carrying the reason");
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
}
