namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Eggs;
using Microsoft.EntityFrameworkCore;

// #406 — standalone stock write-off / reconciliation against a single egg
// lot: available moves, production never restates, the ledger stays the
// source of truth for the cached balance.
[Collection(IntegrationCollection.Name)]
public sealed class EggLotWriteOffTests(CluckworkWebApplicationFactory factory)
{
    private static object Body(string type = "Discard", int delta = -10, string reason = "cooler breakage") =>
        new { movementType = type, quantityDelta = delta, reason };

    private static string Url(Guid lotId) => $"/api/v1/stock/lots/{lotId}/movements";

    private async Task<(Guid accountId, Guid lotId, HttpClient client)> SetupAsync(
        int quantity = 100, DateOnly? restrictedUntil = null)
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var grades = await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large");
        var lotId = await factory.SeedEggLotAsync(accountId, grades["Large"], quantity, restrictedUntil);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (accountId, lotId, client);
    }

    [Fact]
    public async Task Discard_RemovesStock_ProductionUntouched_LedgerBalances()
    {
        var (accountId, lotId, client) = await SetupAsync(100);

        var response = await client.PostWithKeyAsync(Url(lotId), Guid.NewGuid().ToString(), Body("Discard", -10));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(90, payload.GetProperty("quantityAvailable").GetInt32());
        Assert.Equal("Discard", payload.GetProperty("movementType").GetString());
        Assert.Equal(-10, payload.GetProperty("quantityDelta").GetInt32());

        var (produced, available, ledgerSum, discardCount) =
            await factory.WithTenantScopeAsync(accountId, async db =>
            {
                var lot = await db.EggLots.SingleAsync(l => l.Id == lotId);
                var movements = await db.EggInventoryMovements.Where(m => m.EggLotId == lotId).ToListAsync();
                return (lot.QuantityProduced, lot.QuantityAvailable,
                        movements.Sum(m => m.QuantityDelta),
                        movements.Count(m => m.MovementType == EggMovementType.Discard));
            });
        Assert.Equal(100, produced); // the day's laying is not restated
        Assert.Equal(90, available);
        Assert.Equal(available, ledgerSum); // #101 invariant
        Assert.Equal(1, discardCount);
    }

    [Fact]
    public async Task Movement_AppearsInLotLedgerGet_WithReason()
    {
        var (_, lotId, client) = await SetupAsync(50);

        var post = await client.PostWithKeyAsync(Url(lotId), Guid.NewGuid().ToString(),
            Body("InternalUse", -6, "household breakfast"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>(Url(lotId));
        var rows = list.EnumerateArray().ToList();
        Assert.Contains(rows, r =>
            r.GetProperty("movementType").GetString() == "InternalUse"
            && r.GetProperty("quantityDelta").GetInt32() == -6
            && r.GetProperty("reason").GetString() == "household breakfast");
    }

    [Fact]
    public async Task Reconciliation_PositiveWithinProduced_AddsBack()
    {
        var (_, lotId, client) = await SetupAsync(100);

        var discard = await client.PostWithKeyAsync(Url(lotId), Guid.NewGuid().ToString(), Body("Discard", -30));
        Assert.Equal(HttpStatusCode.OK, discard.StatusCode);

        var response = await client.PostWithKeyAsync(Url(lotId), Guid.NewGuid().ToString(),
            Body("Reconciliation", 5, "recount found a tray"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(75, payload.GetProperty("quantityAvailable").GetInt32());
    }

    [Fact]
    public async Task Reconciliation_WithNothingWrittenOff_422()
    {
        // A recount can only restore what write-offs removed. On a lot with no
        // write-off history, any positive delta means production or a sale is
        // wrong — those have their own paths.
        var (_, lotId, client) = await SetupAsync(100);

        var response = await client.PostWithKeyAsync(Url(lotId), Guid.NewGuid().ToString(),
            Body("Reconciliation", 1, "found extra"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EggLot.ReconcileExceedsWrittenOff", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Reconciliation_CannotReAddAllocatedEggs()
    {
        // Security review of this PR: Allocate lowers Available without
        // touching Produced, so a produced-only ceiling reads allocation as
        // headroom — a +40 recount would offer 40 committed eggs for sale
        // again AND make voiding the order impossible (Restore would exceed
        // produced). The written-off cap closes both.
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var grades = await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large");
        var lotId = await factory.SeedEggLotAsync(accountId, grades["Large"], 100);
        var orderId = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 40);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var confirm = await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode); // available now 60

        var response = await client.PostWithKeyAsync(Url(lotId), Guid.NewGuid().ToString(),
            Body("Reconciliation", 40, "recount"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EggLot.ReconcileExceedsWrittenOff", problem.GetProperty("title").GetString());

        var available = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.EggLots.SingleAsync(l => l.Id == lotId)).QuantityAvailable);
        Assert.Equal(60, available); // the allocation stays spendable exactly once
    }

    [Fact]
    public async Task Reconciliation_CappedAtCumulativeWriteOffs()
    {
        var (_, lotId, client) = await SetupAsync(100);

        var discard = await client.PostWithKeyAsync(Url(lotId), Guid.NewGuid().ToString(), Body("Discard", -10));
        Assert.Equal(HttpStatusCode.OK, discard.StatusCode);

        var tooMuch = await client.PostWithKeyAsync(Url(lotId), Guid.NewGuid().ToString(),
            Body("Reconciliation", 11, "recount"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMuch.StatusCode);
        var problem = await tooMuch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EggLot.ReconcileExceedsWrittenOff", problem.GetProperty("title").GetString());

        var exact = await client.PostWithKeyAsync(Url(lotId), Guid.NewGuid().ToString(),
            Body("Reconciliation", 10, "all found again"));
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        var payload = await exact.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(100, payload.GetProperty("quantityAvailable").GetInt32());
    }

    [Fact]
    public async Task Discard_BelowZeroAvailable_422()
    {
        var (accountId, lotId, client) = await SetupAsync(20);

        var response = await client.PostWithKeyAsync(Url(lotId), Guid.NewGuid().ToString(), Body("Discard", -21));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EggLot.InsufficientStock", problem.GetProperty("title").GetString());

        var available = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.EggLots.SingleAsync(l => l.Id == lotId)).QuantityAvailable);
        Assert.Equal(20, available); // unchanged
    }

    [Fact]
    public async Task Discard_CannotConsumeAllocatedEggs()
    {
        // Sell 40 of 100, then try to write off 61 — only 60 remain unsold.
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var grades = await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large");
        var lotId = await factory.SeedEggLotAsync(accountId, grades["Large"], 100);
        var orderId = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 40);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var confirm = await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var tooMuch = await client.PostWithKeyAsync(Url(lotId), Guid.NewGuid().ToString(), Body("Discard", -61));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMuch.StatusCode);

        var exact = await client.PostWithKeyAsync(Url(lotId), Guid.NewGuid().ToString(),
            Body("Discard", -60, "remaining stock broke"));
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        var payload = await exact.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, payload.GetProperty("quantityAvailable").GetInt32());
    }

    [Fact]
    public async Task RestrictedLot_WriteOffAllowed()
    {
        var (_, lotId, client) = await SetupAsync(
            100, restrictedUntil: DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(7));

        var response = await client.PostWithKeyAsync(Url(lotId), Guid.NewGuid().ToString(),
            Body("Discard", -10, "spoiled under withdrawal"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ForeignOrMissingLot_404()
    {
        var (_, _, client) = await SetupAsync(100);

        // Another tenant's lot — reads as null through the tenant filter.
        var otherAccount = await factory.SeedAccountWithUserAsync($"o-{Guid.NewGuid():N}@test.local");
        var otherGrades = await factory.SeedEggGradesAsync(otherAccount, Guid.NewGuid(), "Large");
        var foreignLot = await factory.SeedEggLotAsync(otherAccount, otherGrades["Large"], 10);

        var foreign = await client.PostWithKeyAsync(Url(foreignLot), Guid.NewGuid().ToString(), Body());
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        var missing = await client.PostWithKeyAsync(Url(Guid.NewGuid()), Guid.NewGuid().ToString(), Body());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("Discard", -1, "")]        // reason required
    [InlineData("Sale", -1, "why")]        // ledger-only type
    [InlineData("Discard", 1, "why")]      // a discard cannot add eggs
    [InlineData("Discard", 0, "why")]      // zero moves nothing
    public async Task InvalidBody_400(string type, int delta, string reason)
    {
        var (_, lotId, client) = await SetupAsync(100);
        var response = await client.PostWithKeyAsync(Url(lotId), Guid.NewGuid().ToString(),
            Body(type, delta, reason));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WriteOff_WritesAuditRow()
    {
        var (accountId, lotId, client) = await SetupAsync(100);

        var response = await client.PostWithKeyAsync(Url(lotId), Guid.NewGuid().ToString(),
            Body("Discard", -10, "dropped a tray"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var audit = await factory.WithTenantScopeAsync(accountId, async db =>
            await db.AuditEvents.Where(a => a.Action == "EggLot.Movement").ToListAsync());
        var row = Assert.Single(audit);
        Assert.Equal(lotId, row.EntityId);
        Assert.Equal("dropped a tray", row.Reason);
    }

    // Pins this endpoint's participation in the idempotency protocol (the
    // middleware itself is proven in IdempotencyReplayTests): a replayed key
    // returns the original response and never decrements the lot twice.
    [Fact]
    public async Task SameKey_ReplaysResponse_AndDecrementsOnce()
    {
        var (accountId, lotId, client) = await SetupAsync(100);
        var key = Guid.NewGuid().ToString();

        var first = await client.PostWithKeyAsync(Url(lotId), key, Body("Discard", -10));
        var second = await client.PostWithKeyAsync(Url(lotId), key, Body("Discard", -10));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());

        var (available, discardCount) = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var lot = await db.EggLots.SingleAsync(l => l.Id == lotId);
            return (lot.QuantityAvailable, await db.EggInventoryMovements
                .CountAsync(m => m.EggLotId == lotId && m.MovementType == EggMovementType.Discard));
        });
        Assert.Equal(90, available); // once, not twice
        Assert.Equal(1, discardCount);
    }

    // AGENTS.md: every new aggregate mutation gets a parallel-race test. Two
    // concurrent write-offs each draining the whole lot must serialize on the
    // FOR UPDATE lock — one wins, one is refused, the lot never goes negative
    // and the ledger still sums to the cached balance.
    [Fact]
    public async Task TwoRacingWriteOffs_OneWins_OneRefused_LedgerBalances()
    {
        var (accountId, lotId, client) = await SetupAsync(100);

        var a = client.PostWithKeyAsync(Url(lotId), Guid.NewGuid().ToString(), Body("Discard", -100, "race A"));
        var b = client.PostWithKeyAsync(Url(lotId), Guid.NewGuid().ToString(), Body("Discard", -100, "race B"));
        var responses = await Task.WhenAll(a, b);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));

        var (available, ledgerSum, discardCount) = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var lot = await db.EggLots.SingleAsync(l => l.Id == lotId);
            var movements = await db.EggInventoryMovements.Where(m => m.EggLotId == lotId).ToListAsync();
            return (lot.QuantityAvailable, movements.Sum(m => m.QuantityDelta),
                    movements.Count(m => m.MovementType == EggMovementType.Discard));
        });
        Assert.Equal(0, available);
        Assert.Equal(available, ledgerSum);
        Assert.Equal(1, discardCount); // the loser's ledger row rolled back
    }
}
