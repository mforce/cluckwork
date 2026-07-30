namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// tech spec §11 / functional §23: replaying a write with the same Idempotency-Key must
// return the original response and never duplicate the side effect.
[Collection(IntegrationCollection.Name)]
public sealed class IdempotencyReplayTests(CluckworkWebApplicationFactory factory)
{
    private static object DailyEntryBody(Guid farmId, Guid houseId, Guid flockId) => new
    {
        farmId,
        houseId,
        flockId,
        date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
        totalEggs = 200,
        crackedEggs = 3,
        dirtyEggs = 2,
        discardedEggs = 1,
        mortalityCount = 0
    };

    [Fact]
    public async Task SameKey_ReplaysResponse_AndCreatesRowOnce()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var farmId = Guid.NewGuid();
        var houseId = Guid.NewGuid();
        var flockId = await factory.SeedFlockAsync(accountId, farmId, houseId);
        var body = DailyEntryBody(farmId, houseId, flockId);
        var key = Guid.NewGuid().ToString();

        var first = await client.PostWithKeyAsync("/api/v1/daily-entries", key, body);
        var second = await client.PostWithKeyAsync("/api/v1/daily-entries", key, body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());

        // Exactly one row persisted for the natural key despite two POSTs.
        var count = await factory.WithTenantScopeAsync(accountId, db =>
            db.DailyEntries.CountAsync(e =>
                e.FarmId == farmId && e.HouseId == houseId && e.FlockId == flockId));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SameKey_ConcurrentRequests_OnlyOneSideEffect()
    {
        // Serialization smoke test for #289's fix. The actual race needed three
        // actors (A completes, B in critical section, A's TryRemove unmaps the
        // semaphore B holds, then C GetOrAdds a fresh semaphore alongside B) —
        // unreproducible deterministically over HTTP without a controllable
        // next delegate. Two concurrent requests with no prior completion both
        // share the same semaphore in the old code (nothing has been removed
        // yet), so they always serialise even before the fix. This test verifies
        // the invariant they both exercise: exactly one side effect.
        //
        // Uses an expense as the probe (append-only, no natural-key uniqueness)
        // so that Assert.Equal(1, count) is unambiguous — a double execution
        // would leave two rows.
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        // Seed an expense category (the expense FK target). The handler
        // hardcodes SeedDefaults.FarmId — the category must match.
        var categoryId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.ExpenseCategories.Add(Cluckwork.Domain.Expenses.ExpenseCategory.Create(
                categoryId, accountId, Cluckwork.Domain.Accounts.SeedDefaults.FarmId,
                "Test-Category"));
            await db.SaveChangesAsync();
        });

        var body = new
        {
            expenseCategoryId = categoryId,
            date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            description = "Test expense",
            amountMinorUnits = 10_00L,
            flockId = (Guid?)null,
            note = (string?)null
        };
        var key = Guid.NewGuid().ToString();

        // Fire both requests concurrently, interleaving on the same key.
        var taskA = client.PostWithKeyAsync("/api/v1/expenses", key, body);
        var taskB = client.PostWithKeyAsync("/api/v1/expenses", key, body);
        var responses = await Task.WhenAll(taskA, taskB);

        // Both get a success response (the stripe serializes them; the second
        // sees the replay record and returns the cached response).
        foreach (var r in responses)
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);

        var bodyA = await responses[0].Content.ReadAsStringAsync();
        var bodyB = await responses[1].Content.ReadAsStringAsync();
        Assert.Equal(bodyA, bodyB);

        // Exactly one row persisted despite the concurrent requests. An expense
        // has no natural-key uniqueness, so count > 1 would be unambiguous
        // evidence of a duplicated side effect.
        var count = await factory.WithTenantScopeAsync(accountId, db =>
            db.Expenses.CountAsync(e => e.ExpenseCategoryId == categoryId));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task MissingKey_Returns400()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var response = await client.PostAsJsonAsync("/api/v1/daily-entries",
            DailyEntryBody(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
