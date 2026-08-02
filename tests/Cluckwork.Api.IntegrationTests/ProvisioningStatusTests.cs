namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #283 follow-up — GET /api/v1/auth/provisioning, the first-run hint the SPA
// login page reads.
//
// Own factory (own database, like BootstrapAdminCommandTests): the whole point
// is observing the un-provisioned state, which any test that creates an Owner
// in the shared default account would destroy.
public sealed class ProvisioningStatusTests(CluckworkWebApplicationFactory factory)
    : IClassFixture<CluckworkWebApplicationFactory>
{
    private sealed record StatusPayload(bool Provisioned);

    private async Task<bool> GetProvisionedAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/auth/provisioning");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<StatusPayload>();
        Assert.NotNull(payload);
        return payload!.Provisioned;
    }

    // Creates a user carrying the Owner role under an explicit account. The
    // password is generated at runtime — never a literal, per AGENTS.md
    // ("Generate test credentials at runtime"; GitGuardian flags literals even
    // in test files).
    private async Task CreateOwnerInAsync(Guid accountId, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            // Set explicitly: TenantStampInterceptor only fills an AccountId
            // that is still Guid.Empty, so this survives the save and lets the
            // cross-account case below be set up truthfully.
            AccountId = accountId,
        };

        var created = await users.CreateAsync(user, TemporaryPassword.Generate());
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

        var assigned = await users.AddToRoleAsync(user, Roles.Owner);
        Assert.True(assigned.Succeeded, string.Join("; ", assigned.Errors.Select(e => e.Description)));
    }

    // One method on purpose: the three states must be observed in order against
    // one database, and xUnit gives no ordering guarantee between methods.
    //
    // It also pins the two properties that are easiest to get wrong here:
    //
    //  * The false -> true transition is observed through ONE long-lived host,
    //    so FirstRunProvisioningLatch is proven not to cache a `false`. A latch
    //    that memoised both directions would leave a real operator staring at
    //    "no administrator yet" forever after provisioning, and every
    //    single-request test would still pass.
    //  * An Owner in a DIFFERENT account must not count. This is the assertion
    //    that fails if the AccountId predicate is dropped — without it the
    //    query answers "some Owner exists anywhere", which is not the question.
    [Fact]
    public async Task ReportsUnprovisioned_IgnoresOwnersInOtherAccounts_AndFlipsOnceTheDefaultAccountHasOne()
    {
        var client = factory.CreateClient();

        // 1. Freshly migrated: base reference data exists (account, roles,
        //    grades) but no user does, because no credential is ever
        //    migration-baked.
        Assert.False(await GetProvisionedAsync(client),
            "a freshly migrated database has no Owner, so the hint must show");

        // 2. An Owner under a different account is not this account's Owner.
        await CreateOwnerInAsync(
            Guid.NewGuid(), $"other-account-owner-{Guid.NewGuid():N}@test.local");

        Assert.False(await GetProvisionedAsync(client),
            "an Owner in another account must not satisfy the default account's first run");

        // 3. The real thing.
        await CreateOwnerInAsync(
            SeedDefaults.AccountId, $"default-account-owner-{Guid.NewGuid():N}@test.local");

        Assert.True(await GetProvisionedAsync(client),
            "once the default account has an Owner the hint must stop showing");

        // The other half of the latch contract, and it has to live HERE rather
        // than in OnceLatched_... below: that test latches by hand, so it proves
        // a latched service short-circuits but says nothing about the service
        // ever SETTING the latch. Deleting `if (provisioned) latch.Latch();`
        // leaves it perfectly green. This assertion is what fails.
        //
        // Read from the running host's own singleton, so it describes the
        // request that just went over HTTP — not a latch this test constructed.
        Assert.True(
            factory.Services.GetRequiredService<FirstRunProvisioningLatch>().IsProvisioned,
            "a `true` observation must latch, or every later request re-queries the database");
    }

    // Anonymous by necessity — the only caller is a visitor with no account.
    // Asserted explicitly because a stray RequireAuthorization() would make the
    // endpoint 401 exactly when it is supposed to be useful, and the SPA's
    // catch would quietly render nothing rather than fail.
    [Fact]
    public async Task IsReachableWithoutAnyCredential()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/provisioning");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    // #312's no-store default must reach this response. A cached `false` would
    // outlive provisioning and keep telling an operational instance to run
    // bootstrap-admin — the one failure mode that would survive a restart.
    [Fact]
    public async Task IsNotCacheable()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/provisioning");

        // Asserted as DIRECTIVES, not as a string. Cache-Control is a typed
        // header, so HttpClient parses and re-serializes it on the way in
        // ("no-store, private") no matter how it is read — TryGetValues does
        // not recover the wire text either. Comparing strings would pin
        // HttpClient's formatting rather than this app's behaviour, and would
        // break on a directive order that is semantically identical.
        var cacheControl = response.Headers.CacheControl;
        Assert.NotNull(cacheControl);
        Assert.True(cacheControl!.NoStore, "must not be stored by any cache");
        Assert.True(cacheControl.Private, "must never be held by a shared cache");
    }

    // PR #359 review — the latch's whole job is to stop touching the database
    // once an Owner exists, and NOTHING above tested that: deleting
    // `if (provisioned) latch.Latch();` outright leaves every other test in this
    // file green, because the raw query keeps returning the right answer on
    // every call. The answer being correct is not the property; not asking is.
    //
    // Proven both ways against a context aimed at a port nothing listens on, so
    // "did it query?" is directly observable rather than inferred:
    //   * cold latch  -> must reach the database, so it must THROW
    //   * latched     -> must answer from memory, so it must NOT throw
    // A one-sided version (only the second half) would pass just as happily
    // against a service that never queries at all.
    [Fact]
    public async Task OnceLatched_AnswersWithoutTouchingTheDatabase()
    {
        var unreachable = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1")
            .Options;
        await using var unusableDb = new AppDbContext(unreachable, new TenantContext());

        using var scope = factory.Services.CreateScope();
        var normalizer = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>();

        // A FRESH latch, deliberately not the host's singleton — latching that
        // one would leak "provisioned" into the other tests in this class, and
        // xUnit does not order methods.
        var latch = new FirstRunProvisioningLatch();

        await Assert.ThrowsAnyAsync<Exception>(
            () => new FirstRunStatusService(unusableDb, normalizer, latch).IsProvisionedAsync());

        latch.Latch();

        Assert.True(
            await new FirstRunStatusService(unusableDb, normalizer, latch).IsProvisionedAsync(),
            "a latched instance must answer from memory, never from the database");
    }

    // Existence only. If this ever grows an email or a count, an un-provisioned
    // instance starts telling an anonymous caller something a provisioned one
    // does not — which is the line this endpoint was designed not to cross.
    [Fact]
    public async Task ExposesNothingBeyondTheBooleanItself()
    {
        var client = factory.CreateClient();

        var json = await client.GetStringAsync("/api/v1/auth/provisioning");

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var properties = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["provisioned"], properties);
    }
}
