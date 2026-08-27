namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #283 follow-up — how a sign-in on an instance with NO administrator reports
// itself, so the login screen can explain the dead end instead of showing the
// generic denial.
//
// Replaces an earlier `GET /api/v1/auth/provisioning` that the login page
// polled on mount. That answered anyone who asked and reached the database on
// every anonymous page load throughout the window before setup; this rides the
// 401 that login already returns, so nothing extra is requested and only
// someone actually attempting a sign-in learns anything.
//
// Own factory (own database, like BootstrapAdminCommandTests): the whole point
// is observing the un-provisioned state, which any test that creates an Owner
// in the shared default account would destroy.
public sealed class FirstRunLoginNoticeTests(CluckworkWebApplicationFactory factory)
    : IClassFixture<CluckworkWebApplicationFactory>
{
    // #532 — instance, not static: resolves the farm code of the account the
    // email actually belongs to, falling back to the default farm for the
    // deliberately-unknown addresses this suite probes with.
    private async Task<(HttpStatusCode Status, string? Title)> AttemptLoginAsync(
        HttpClient client, string email, string password)
    {
        var farmCode = await factory.FarmCodeForAsync(email);
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { farmCode, email, password });
        var problem = await response.Content
            .ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return (response.StatusCode,
            problem.TryGetProperty("title", out var title) ? title.GetString() : null);
    }

    // Password generated at runtime — never a literal, per AGENTS.md
    // ("Generate test credentials at runtime"; GitGuardian flags literals even
    // in test files).
    private async Task CreateOwnerInAsync(Guid accountId, string email)
    {
        // #532 — the account must EXIST before a user can reference it.
        // AspNetUsers.AccountId is now a real foreign key
        // (FK_AspNetUsers_Accounts_AccountId), so the previous shape — a user
        // pointing at a bare Guid.NewGuid() — is refused by Postgres with
        // 23503. The test's intent is unchanged: an Owner under a DIFFERENT
        // account, which must now be a real one.
        // Guarded: this helper is also called for the DEFAULT account (step 2b
        // seeds a non-Owner there), whose row InitialCreate already inserted.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var exists = await db.Accounts.IgnoreQueryFilters()
                .AnyAsync(a => a.Id == accountId);
            if (exists) return;

            db.Accounts.Add(Account.Create(
                accountId, "Other Farm Co", "farm-" + accountId.ToString("N")[..12], "UTC", "USD"));
            await db.SaveChangesAsync();
        });

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            // Set explicitly: TenantStampInterceptor only fills an AccountId
            // that is still Guid.Empty.
            AccountId = accountId,
        };

        var created = await users.CreateAsync(user, TemporaryPassword.Generate());
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

        var assigned = await users.AddToRoleAsync(user, Roles.Owner);
        Assert.True(assigned.Succeeded, string.Join("; ", assigned.Errors.Select(e => e.Description)));
    }

    // One method on purpose: the states must be observed in order against one
    // database, and xUnit gives no ordering guarantee between methods.
    //
    // The second half is the one that matters most. A build that returned the
    // first-run code for EVERY failed sign-in would pass a test that only
    // checked the un-provisioned case — and would tell a user who simply
    // mistyped their password that the farm has no administrator. The code must
    // disappear once the DEFAULT ACCOUNT has an Owner, leaving the ordinary
    // non-enumerating denial byte-for-byte as it was.
    //
    // "Default account", not "an Owner anywhere" and not "any account exists":
    // steps 2 and 2b below create an Owner under a different account and a
    // non-Owner under this one, and the notice still fires through both. Those
    // steps exist precisely to pin that scope, so a summary here that claimed
    // otherwise would be refuted by the body of its own test (PR #363 review
    // rounds 5 and 6).
    [Fact]
    public async Task ReportsNoOwner_UntilTheDefaultAccountHasOne_ThenTheOrdinaryDenialReturns()
    {
        var client = factory.CreateClient();

        // 1. Freshly migrated: base reference data exists, no user does.
        var beforeProvisioning = await AttemptLoginAsync(client, "nobody@test.local", "whatever");
        Assert.Equal(HttpStatusCode.Unauthorized, beforeProvisioning.Status);
        Assert.Equal(AuthEndpoints.NoOwnerProvisionedCode, beforeProvisioning.Title);

        // 2. An Owner under a DIFFERENT account is not this account's Owner, so
        //    the default account is still un-provisioned. Pins the AccountId
        //    predicate: without it the query answers "any Owner anywhere".
        await CreateOwnerInAsync(
            Guid.NewGuid(), $"other-account-owner-{Guid.NewGuid():N}@test.local");

        var crossAccount = await AttemptLoginAsync(client, "nobody@test.local", "whatever");
        Assert.Equal(AuthEndpoints.NoOwnerProvisionedCode, crossAccount.Title);

        // 2b. A NON-OWNER user in the default account does not count either, and
        //     this is the state the copy had to be reworded for (#363 review):
        //     the seeders create Workers/Managers without ever running
        //     bootstrap-admin, and such a user signs in perfectly well (see
        //     ASuccessfulSignIn_NeverReportsIt). If they mistype their password
        //     here they are told there is no administrator — which is TRUE, and
        //     is why the copy says "no administrator account" rather than "no
        //     accounts" or "no sign-in can succeed".
        //
        //     Pinned deliberately: the behaviour reads oddly out of context, so
        //     it is exactly the kind of thing a later change "corrects" into a
        //     regression. If this is ever revisited, the fix is the predicate
        //     (and #283's invariant), not a quiet special case here.
        var nonOwnerEmail = $"non-owner-{Guid.NewGuid():N}@test.local";
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var created = await users.CreateAsync(
                new ApplicationUser
                {
                    UserName = nonOwnerEmail,
                    Email = nonOwnerEmail,
                    AccountId = SeedDefaults.AccountId,
                },
                TemporaryPassword.Generate());
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        var nonOwnerWrongPassword = await AttemptLoginAsync(
            client, nonOwnerEmail, "definitely-not-the-password");
        Assert.Equal(AuthEndpoints.NoOwnerProvisionedCode, nonOwnerWrongPassword.Title);

        // 3. The real thing — from here the response must be indistinguishable
        //    from any other wrong-credential attempt.
        var ownerEmail = $"default-account-owner-{Guid.NewGuid():N}@test.local";
        await CreateOwnerInAsync(SeedDefaults.AccountId, ownerEmail);

        var unknownAddress = await AttemptLoginAsync(client, "nobody@test.local", "whatever");
        Assert.Equal(HttpStatusCode.Unauthorized, unknownAddress.Status);
        Assert.NotEqual(AuthEndpoints.NoOwnerProvisionedCode, unknownAddress.Title);

        // A wrong password for a user that genuinely EXISTS must answer
        // identically to the unknown address above — this feature must not have
        // made the denial enumerable.
        //
        // Uses the Owner's real, just-created address (PR #363 review). An
        // earlier version submitted an unrelated literal here, so BOTH sides of
        // the comparison were unknown-user requests: it asserted that two
        // identical code paths agree, and would have stayed green even if
        // known-user failures started returning something distinguishable.
        var knownAddress = await AttemptLoginAsync(client, ownerEmail, "definitely-not-the-password");
        Assert.Equal(unknownAddress.Status, knownAddress.Status);
        Assert.Equal(unknownAddress.Title, knownAddress.Title);

        // And the latch must have engaged, or every later failed sign-in
        // re-queries. Read from the running host's own singleton, so it
        // describes the requests that just went over HTTP.
        Assert.True(
            factory.Services.GetRequiredService<FirstRunProvisioningLatch>().IsProvisioned,
            "a `true` observation must latch, or every later failure re-queries the database");
    }

    // The check must never run for a request that succeeds — that is what keeps
    // it off the hot path entirely.
    [Fact]
    public async Task ASuccessfulSignIn_NeverReportsIt()
    {
        var email = $"successful-login-{Guid.NewGuid():N}@test.local";
        var password = TemporaryPassword.Generate();

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                AccountId = SeedDefaults.AccountId,
            };
            var created = await users.CreateAsync(user, password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { farmCode = TestHarness.DefaultFarmCode, email, password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(AuthEndpoints.NoOwnerProvisionedCode, body, StringComparison.Ordinal);
    }

    // The endpoint the earlier design added is gone. Asserted rather than
    // assumed: leaving it mapped would keep the anonymous, pollable surface
    // this change exists to remove, and nothing else would fail.
    [Fact]
    public async Task TheOldPolledStatusEndpointNoLongerExists()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/provisioning");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // PR #359 review — the latch's job is to stop touching the database once
    // the default account has an Owner, and the answer being correct is not
    // that property; not asking is. Proven both ways against a context aimed at a port nothing
    // listens on, so "did it query?" is directly observable:
    //   * cold latch -> must reach the database, so it must THROW
    //   * latched    -> must answer from memory, so it must NOT throw
    // A one-sided version would pass against a service that never queries.
    [Fact]
    public async Task OnceLatched_AnswersWithoutTouchingTheDatabase()
    {
        var unreachable = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1")
            .Options;
        await using var unusableDb = new AppDbContext(unreachable, new TenantContext(), new FlockScope());

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
}
