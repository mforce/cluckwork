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
    private static async Task<(HttpStatusCode Status, string? Title)> AttemptLoginAsync(
        HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { email, password });
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
    // mistyped their password that the farm has no accounts. The code must
    // disappear the moment an Owner exists, leaving the ordinary
    // non-enumerating denial byte-for-byte as it was.
    [Fact]
    public async Task ReportsNoAccounts_UntilAnOwnerExists_ThenTheOrdinaryDenialReturns()
    {
        var client = factory.CreateClient();

        // 1. Freshly migrated: base reference data exists, no user does.
        var beforeProvisioning = await AttemptLoginAsync(client, "nobody@test.local", "whatever");
        Assert.Equal(HttpStatusCode.Unauthorized, beforeProvisioning.Status);
        Assert.Equal(AuthEndpoints.NoAccountsProvisionedCode, beforeProvisioning.Title);

        // 2. An Owner under a DIFFERENT account is not this account's Owner, so
        //    the default account is still un-provisioned. Pins the AccountId
        //    predicate: without it the query answers "any Owner anywhere".
        await CreateOwnerInAsync(
            Guid.NewGuid(), $"other-account-owner-{Guid.NewGuid():N}@test.local");

        var crossAccount = await AttemptLoginAsync(client, "nobody@test.local", "whatever");
        Assert.Equal(AuthEndpoints.NoAccountsProvisionedCode, crossAccount.Title);

        // 3. The real thing — from here the response must be indistinguishable
        //    from any other wrong-credential attempt.
        await CreateOwnerInAsync(
            SeedDefaults.AccountId, $"default-account-owner-{Guid.NewGuid():N}@test.local");

        var afterProvisioning = await AttemptLoginAsync(client, "nobody@test.local", "whatever");
        Assert.Equal(HttpStatusCode.Unauthorized, afterProvisioning.Status);
        Assert.NotEqual(AuthEndpoints.NoAccountsProvisionedCode, afterProvisioning.Title);

        // A wrong password for a user that DOES exist must answer identically to
        // the unknown address above — the non-enumerating denial is unchanged by
        // this feature.
        var knownAddress = await AttemptLoginAsync(
            client, $"default-account-owner-probe@test.local", "whatever");
        Assert.Equal(afterProvisioning.Title, knownAddress.Title);

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
            "/api/v1/auth/login", new { email, password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(AuthEndpoints.NoAccountsProvisionedCode, body, StringComparison.Ordinal);
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

    // PR #359 review — the latch's job is to stop touching the database once an
    // Owner exists, and the answer being correct is not that property; not
    // asking is. Proven both ways against a context aimed at a port nothing
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
}
