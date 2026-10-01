namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Flocks;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #536 Part 2 — two-farm isolation matrix. Two farms are provisioned THROUGH
// AccountProvisioner.ProvisionAsync (not the seeders — #533's parity guard and
// this fixture must describe the same farm), both owners log in with the
// returned temporary passwords, and farm B drives the full egg loop. Then:
//
//   * VISIBILITY: farm A observes none of B's rows (counts by entity, incl.
//     audit provenance), and vice versa for A's pre-existing row.
//   * NEGATIVE ISOLATION (Q1, resolved as 404 + no mutation for the FOR UPDATE
//     surfaces): A's owner, fully authenticated, tries to confirm / allocate /
//     read B's real entity ids. Every one must be a 404 (masked NotFound — no
//     existence leak) AND leave B's row unmutated.
//
// This is the end-to-end proof the guard (Part 1) protects: the guard fails the
// build on a bypass, and this matrix proves the pipeline actually scopes at
// runtime, with real ids crossing the tenant boundary.
[Collection(IntegrationCollection.Name)]
public sealed class TwoFarmIsolationMatrixTests(CluckworkWebApplicationFactory factory)
{
    // Provision a farm through the real AccountProvisioner and return the
    // outcome (account id + owner's temporary password). The Owner starts with
    // MustChangePassword=true; the test clears it via change-password before
    // driving the loop, exactly as a real first login does.
    private async Task<AccountProvisionOutcome> ProvisionFarmAsync(string slug, string ownerEmail)
    {
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<AccountProvisioner>()
            .ProvisionAsync(name: $"{slug} Farm", slug: slug, ownerEmail: ownerEmail,
                locale: "en-US", currencyCode: "USD");
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Description : string.Empty);
        return result.Value;
    }

    // Log in as the farm's Owner with the temporary password, then clear
    // MustChangePassword (the first-run flow) and return an authed client.
    // The harness's LoginAsync hardcodes TestHarness.Password, but the
    // provisioned Owner has a fresh temporary password and MustChangePassword=
    // true, so this does the login inline with the real credentials.
    private async Task<HttpClient> AuthedOwnerAsync(string ownerEmail, string temporaryPassword, string farmCode)
    {
        var client = factory.CreateClient(TestHarness.Cookieless(factory));
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            farmCode, email = ownerEmail, password = temporaryPassword,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = await login.Content.ReadFromJsonAsync<AccessTokenResponse>();
        Assert.NotNull(tokens);

        var authed = factory.CreateAuthedClient(tokens!.AccessToken);

        // First-run: change the temporary password to clear MustChangePassword.
        // Without this, MustChangePasswordMiddleware 403s everything except
        // change-password/logout (#283).
        //
        // change-password REVOKES every prior session (credential-epoch bump)
        // and returns a FRESH access token in the body (#364). The client that
        // made the change keeps the pre-change token, which is now inert — so
        // rebuild the client from the response's new token. Using the stale
        // token on the next call is a 401, which is exactly what this fixture
        // hit before the rebuild.
        var change = await authed.PostAsJsonAsync("/api/v1/auth/change-password", new
        {
            currentPassword = temporaryPassword,
            newPassword = TestHarness.Password,
        });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var freshTokens = await change.Content.ReadFromJsonAsync<AccessTokenResponse>();
        Assert.NotNull(freshTokens);
        return factory.CreateAuthedClient(freshTokens!.AccessToken);
    }

    [Fact]
    public async Task TwoFarms_FullEggLoop_IsMutuallyInvisible()
    {
        // --- Provision both farms through the real provisioner -------------
        var slugA = "farm-a-" + Guid.NewGuid().ToString("N")[..10];
        var slugB = "farm-b-" + Guid.NewGuid().ToString("N")[..10];
        var emailA = $"a-{Guid.NewGuid():N}@test.local";
        var emailB = $"b-{Guid.NewGuid():N}@test.local";

        var outcomeA = await ProvisionFarmAsync(slugA, emailA);
        var outcomeB = await ProvisionFarmAsync(slugB, emailB);
        Assert.NotEqual(outcomeA.AccountId, outcomeB.AccountId);

        var clientA = await AuthedOwnerAsync(emailA, outcomeA.TemporaryPassword, outcomeA.Slug);
        var clientB = await AuthedOwnerAsync(emailB, outcomeB.TemporaryPassword, outcomeB.Slug);

        // Give farm A one pre-existing row so the "vice versa" direction has
        // something to be invisible to.
        var farmIdA = Guid.NewGuid();
        var gradesA = await factory.SeedEggGradesAsync(outcomeA.AccountId, farmIdA, "Large");
        var lotA = await factory.SeedEggLotAsync(outcomeA.AccountId, gradesA["Large"], 50);

        // --- Farm B drives the full egg loop (real API where practical) ---
        var farmIdB = Guid.NewGuid();
        var flockB = await factory.SeedFlockAsync(outcomeB.AccountId, farmIdB);
        var gradesB = await factory.SeedEggGradesAsync(outcomeB.AccountId, farmIdB, "Large");
        var lotB = await factory.SeedEggLotAsync(outcomeB.AccountId, gradesB["Large"], 100);
        var orderB = await factory.SeedSalesOrderAsync(outcomeB.AccountId, gradesB["Large"], 10);

        // Farm B confirms its own order through the API (the FOR UPDATE lock
        // path). This mutates B's lot (allocation) — the negative isolation
        // assertion below checks A cannot trigger or observe that.
        var confirmB = await clientB.PostWithKeyAsync(
            $"/api/v1/sales/{orderB}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, confirmB.StatusCode);

        // B's lot was decremented by the allocation (100 -> 90).
        var lotBAfter = await factory.WithTenantScopeAsync(outcomeB.AccountId, async db =>
            await db.EggLots.FirstAsync(l => l.Id == lotB));
        Assert.Equal(90, lotBAfter.QuantityAvailable);

        // --- VISIBILITY: A sees none of B's rows --------------------------
        // B's egg lot, sales order, flock, and audit events are invisible to A.
        var bLotsVisibleToA = await factory.WithTenantScopeAsync(outcomeA.AccountId, db =>
            db.EggLots.AnyAsync(l => l.Id == lotB));
        Assert.False(bLotsVisibleToA);

        var bOrdersVisibleToA = await factory.WithTenantScopeAsync(outcomeA.AccountId, db =>
            db.SalesOrders.AnyAsync(o => o.Id == orderB));
        Assert.False(bOrdersVisibleToA);

        var bFlocksVisibleToA = await factory.WithTenantScopeAsync(outcomeA.AccountId, db =>
            db.Flocks.AnyAsync(f => f.Id == flockB));
        Assert.False(bFlocksVisibleToA);

        // A's pre-existing row is invisible to B (the "vice versa" direction):
        // B, scoped to its own account, sees none of A's lots by A's real id.
        var aLotVisibleToB = await factory.WithTenantScopeAsync(outcomeB.AccountId, db =>
            db.EggLots.AnyAsync(l => l.Id == lotA));
        Assert.False(aLotVisibleToB);

        // B sees exactly its own lot (not A's): B's egg-lot count is 1.
        var bLotCount = await factory.WithTenantScopeAsync(outcomeB.AccountId, db =>
            db.EggLots.CountAsync());
        Assert.Equal(1, bLotCount);

        // Audit provenance: B's confirm wrote an audit row for B's order. A must
        // not see it.
        var bAuditVisibleToA = await factory.WithTenantScopeAsync(outcomeA.AccountId, db =>
            db.AuditEvents.AnyAsync(e => e.EntityId == orderB));
        Assert.False(bAuditVisibleToA);

        // --- NEGATIVE ISOLATION (Q1: 404 + no mutation) -------------------
        // A's owner, fully authenticated, targets B's REAL ids. Every one must
        // 404 (masked NotFound) AND leave B's row unmutated.
        //
        // TWO LAYERS uphold the 404, and the mutation proof must defeat BOTH:
        //   (1) the global query filter scopes db.SalesOrders to A's account, so
        //       GetByIdAsync(B's id) returns null → 404 NotFound;
        //   (2) the confirm handler's post-load AccountId check (defense in
        //       depth) returns TenantMismatch → also surfaced as 404.
        // Bypassing only (1) still 404s via (2); removing only (2) still 404s
        // via (1). The real leak is bypassing (1) AND removing (2) — then A's
        // confirm of B's order proceeds (422 NotDraft, already confirmed), and
        // this assertion reds. That is the mutation this matrix is proven against
        // (run: IgnoreQueryFilters on SalesOrderRepository.GetByIdAsync + delete
        // the `order.AccountId != accountId` check in ConfirmSaleHandler).
        //
        // 1. A tries to confirm B's (already-confirmed) order by B's real id.
        //    -> 404, and B's lot stays at 90 (no second allocation).
        var aConfirmsBOrder = await clientA.PostWithKeyAsync(
            $"/api/v1/sales/{orderB}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, aConfirmsBOrder.StatusCode);
        var lotBAfterAttack = await factory.WithTenantScopeAsync(outcomeB.AccountId, async db =>
            await db.EggLots.FirstAsync(l => l.Id == lotB));
        Assert.Equal(90, lotBAfterAttack.QuantityAvailable);

        // 2. A tries to read B's egg lot by B's real id.
        //    -> 404 (no existence leak).
        var aReadsBOrder = await clientA.GetAsync($"/api/v1/sales/{orderB}");
        Assert.Equal(HttpStatusCode.NotFound, aReadsBOrder.StatusCode);

        // 3. A tries to create a daily entry against B's real flock id.
        //    -> 404 (masked NotFound — the flock is not visible to A's tenant,
        //    and the handler maps .NotFound errors to 404, not a 400). The
        //    payload is validation-valid (date = today, so it is not rejected
        //    by DailyEntry.Date.NotFuture before reaching the handler); the
        //    only rejection reason is the tenant boundary. And no entry row is
        //    created in B's farm.
        var aEntryOnBFlock = await clientA.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), new
            {
                farmId = farmIdB,
                houseId = Guid.NewGuid(),
                flockId = flockB,
                date = DateOnly.FromDateTime(DateTime.UtcNow.Date).ToString("yyyy-MM-dd"),
                totalEggs = 5,
                crackedEggs = 0,
                dirtyEggs = 0,
                discardedEggs = 0,
                mortalityCount = 0,
                grades = new[] { new { eggGradeId = gradesB["Large"], quantity = 5 } },
            });
        Assert.Equal(HttpStatusCode.NotFound, aEntryOnBFlock.StatusCode);

        // No B-farm daily entry was created by A's attempt.
        var entriesOnBFlock = await factory.WithTenantScopeAsync(outcomeB.AccountId, db =>
            db.DailyEntries.CountAsync(e => e.FlockId == flockB));
        // B created none (the loop above seeded a lot, not a daily entry); A's
        // attempt must not have added one either.
        Assert.Equal(0, entriesOnBFlock);
    }
}
