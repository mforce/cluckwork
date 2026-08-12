namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #500 — who signs the demo fixture.
//
// Both facts below need a database in a state the shared IntegrationCollection
// cannot promise: one with NO Owner, and one with EXACTLY ONE Owner and no
// prior demo seed. `DemoSeedTests` shares a container and SeedDefaults.AccountId
// across its facts and already documents its reliance on xUnit's default
// sequencing — there is no ITestCaseOrderer anywhere in this project. Two more
// order-dependent facts in that file would be a coin flip:
//
//   * the no-Owner fact would find an Owner a sibling had provisioned, and the
//     seeder would answer AlreadySeeded instead of PrerequisitesMissing;
//   * the attribution fact would either find the demo data already seeded (so
//     its own SeedAsync writes nothing and every Assert.All passes vacuously),
//     or find several Owners — and FindOwnerAsync deterministically takes the
//     lowest Id, which need not be the one the test created.
//
// So each gets its own factory, hence its own Postgres container
// (CluckworkWebApplicationFactory holds a per-instance PostgreSqlContainer),
// the same isolation SimulationSeedFactory already uses. Correctness here does
// not depend on execution order at all.
public sealed class DemoSeedNoOwnerFactory : CluckworkWebApplicationFactory;

public sealed class DemoSeedNoOwnerTests(DemoSeedNoOwnerFactory factory)
    : IClassFixture<DemoSeedNoOwnerFactory>
{
    // The guard that makes the Owner a real prerequisite rather than a wish.
    //
    // The fixture seeds a lone MANAGER — not an empty account. An empty account
    // would also pass against a FindOwnerAsync that merely asked "does any user
    // exist", which is exactly the bug worth catching: the role is the point.
    [Fact]
    public async Task DemoSeed_WithNoOwner_FailsClosed()
    {
        await factory.SeedUserAsync(
            SeedDefaults.AccountId, $"manager-{Guid.NewGuid():N}@test.local", Roles.Manager);

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();

        Assert.Equal(SeedStatus.PrerequisitesMissing, result.Status);
        Assert.False(result.IsSuccess);
        // Names the fix, not just the failure — this message is the whole
        // interface between the guard and the person who tripped it.
        Assert.Contains("bootstrap-admin", result.Message);

        // Fails closed, not half-open: the message alone would pass for a
        // seeder that had already written half a farm before giving up.
        var flocks = await factory.WithTenantScopeAsync(SeedDefaults.AccountId, async db =>
            await db.Flocks.IgnoreQueryFilters().CountAsync(f => f.AccountId == SeedDefaults.AccountId));
        Assert.Equal(0, flocks);
    }
}

public sealed class DemoSeedAttributionFactory : CluckworkWebApplicationFactory;

public sealed class DemoSeedAttributionTests(DemoSeedAttributionFactory factory)
    : IClassFixture<DemoSeedAttributionFactory>
{
    // Every row the demo seeder writes names the Owner.
    //
    // Asserted POSITIVELY — equal to the Owner's id AND email — never as
    // "!= (unresolved)". The negative form passes for any wrong-but-non-
    // placeholder value, which is most of the ways this can regress: a stale
    // actor from a previous phase, another account's Owner, an empty id beside
    // a correct email.
    [Fact]
    public async Task DemoSeed_AttributesEverySeededAuditEventToTheOwner()
    {
        var email = $"owner-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(SeedDefaults.AccountId, email, Roles.Owner);
        var ownerId = await factory.WithTenantScopeAsync(SeedDefaults.AccountId, async db =>
            await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());

        using (var scope = factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
            Assert.Equal(SeedStatus.Seeded, result.Status);
        }

        var rows = await factory.WithTenantScopeAsync(SeedDefaults.AccountId, async db =>
            await db.AuditEvents.IgnoreQueryFilters()
                .Where(e => e.AccountId == SeedDefaults.AccountId)
                .ToListAsync());

        // Without this, a seeder that wrote no audit rows at all would satisfy
        // Assert.All vacuously — the failure mode this whole issue is about.
        Assert.NotEmpty(rows);
        Assert.All(rows, e =>
        {
            Assert.Equal(ownerId, e.ActorUserId);
            Assert.Equal(email, e.ActorEmail);
        });
    }
}

// #500 (codex review of PR #517) — a DISABLED Owner must never sign the fixture.
//
// Disabling a user keeps their Owner role row and only stamps DisabledAt —
// IdentityProvider says so in its own words: "a disabled actor retains its Owner
// ROLE ROW, only authentication is blocked". So GetUsersInRoleAsync still returns
// them, and a FindOwnerAsync that ordered by Id alone could sign the whole
// fixture with an account that login rejects: every History line naming somebody
// nobody can sign in as, to look at the fixture they supposedly created.
//
// Own factory/container, same reasoning as the two classes above.
public sealed class DemoSeedDisabledOwnerFactory : CluckworkWebApplicationFactory;

public sealed class DemoSeedDisabledOwnerTests(DemoSeedDisabledOwnerFactory factory)
    : IClassFixture<DemoSeedDisabledOwnerFactory>
{
    // Ids are chosen, not generated, so the ORDER is the test's to control:
    // the disabled Owner sorts first, which is precisely the case a filterless
    // OrderBy(Id) gets wrong. With random GUIDs this test would catch the bug
    // only about half the time.
    private static readonly Guid DisabledOwnerId = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid ActiveOwnerId = new("ffffffff-0000-0000-0000-0000000000a2");

    private static async Task<ApplicationUser> AddOwnerAsync(
        IServiceProvider services, Guid id, string email, bool disabled)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roles.RoleExistsAsync(Roles.Owner))
            await roles.CreateAsync(new ApplicationRole { Name = Roles.Owner });

        var user = new ApplicationUser
        {
            Id = id,
            UserName = email,
            Email = email,
            AccountId = SeedDefaults.AccountId,
            DisabledAt = disabled ? DateTime.UtcNow : null,
        };
        var created = await users.CreateAsync(user, TestHarness.Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        Assert.True((await users.AddToRoleAsync(user, Roles.Owner)).Succeeded);
        return user;
    }

    [Fact]
    public async Task DemoSeed_PrefersTheEnabledOwner_EvenWhenADisabledOneSortsFirst()
    {
        await AddOwnerAsync(factory.Services, DisabledOwnerId, "disabled-owner@test.local", disabled: true);
        var active = await AddOwnerAsync(factory.Services, ActiveOwnerId, "active-owner@test.local", disabled: false);

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
        Assert.Equal(SeedStatus.Seeded, result.Status);

        var rows = await factory.WithTenantScopeAsync(SeedDefaults.AccountId, async db =>
            await db.AuditEvents.IgnoreQueryFilters()
                .Where(e => e.AccountId == SeedDefaults.AccountId)
                .ToListAsync());

        Assert.NotEmpty(rows);
        // Positive form: asserting "not the disabled one" would also pass for a
        // seeder that picked some third party.
        Assert.All(rows, e =>
        {
            Assert.Equal(active.Id, e.ActorUserId);
            Assert.Equal(active.Email, e.ActorEmail);
        });
    }
}

// A separate container again, because the fact above needs an enabled Owner to
// exist and this one needs there to be none.
public sealed class DemoSeedOnlyDisabledOwnerFactory : CluckworkWebApplicationFactory;

public sealed class DemoSeedOnlyDisabledOwnerTests(DemoSeedOnlyDisabledOwnerFactory factory)
    : IClassFixture<DemoSeedOnlyDisabledOwnerFactory>
{
    [Fact]
    public async Task DemoSeed_WithOnlyADisabledOwner_FailsClosed()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (!await roles.RoleExistsAsync(Roles.Owner))
                await roles.CreateAsync(new ApplicationRole { Name = Roles.Owner });

            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = "only-disabled@test.local",
                Email = "only-disabled@test.local",
                AccountId = SeedDefaults.AccountId,
                DisabledAt = DateTime.UtcNow,
            };
            Assert.True((await users.CreateAsync(user, TestHarness.Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, Roles.Owner)).Succeeded);
        }

        using var seedScope = factory.Services.CreateScope();
        var result = await seedScope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();

        Assert.Equal(SeedStatus.PrerequisitesMissing, result.Status);

        // The message must name the remedy that actually WORKS for this cause.
        // `bootstrap-admin` counts Owner role rows without checking DisabledAt,
        // so it reports "already provisioned" and exits 0 having done nothing —
        // sending an operator who follows it straight back to this same error.
        // Asserting only "it failed" would have shipped that loop.
        Assert.Contains("DISABLED", result.Message);
        Assert.Contains("will NOT fix this", result.Message);
        Assert.Contains("Re-enable the Owner", result.Message);

        // The message alone would pass for a seeder that had already written
        // half a farm before refusing.
        var flocks = await factory.WithTenantScopeAsync(SeedDefaults.AccountId, async db =>
            await db.Flocks.IgnoreQueryFilters().CountAsync(f => f.AccountId == SeedDefaults.AccountId));
        Assert.Equal(0, flocks);
    }
}
