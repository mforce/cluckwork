namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.Audit;
using Cluckwork.Domain.Auditing;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #494 — "who created this, and who last changed it" derived from the
// append-only audit trail rather than from new columns on every aggregate.
// The lookup is raw SQL (DISTINCT ON), so it bypasses the EF tenant query
// filter: the AccountId predicate in that SQL is the only thing scoping it,
// which is what Provenance_IsScopedToTheTenant exists to hold in place.
[Collection(IntegrationCollection.Name)]
public sealed class AuditProvenanceTests(CluckworkWebApplicationFactory factory)
{
    private static readonly DateTimeOffset Base =
        new(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

    private async Task SeedEventsAsync(Guid accountId, params AuditEvent[] events) =>
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.AuditEvents.AddRange(events);
            await db.SaveChangesAsync();
        });

    // actorUserId defaults to a fresh id, so events are by DIFFERENT people
    // unless a test deliberately passes the same one. Identity is the user id,
    // never the email — the email is a snapshot label that can be re-pointed.
    private static AuditEvent Event(
        Guid accountId, Guid entityId, string action, string email, int minutesFromBase,
        string entityType = "Flock", Guid? actorUserId = null) =>
        AuditEvent.Create(
            Guid.NewGuid(), accountId, Base.AddMinutes(minutesFromBase),
            actorUserId ?? Guid.NewGuid(), email, action, entityType, entityId);

    private async Task<T> WithRepositoryAsync<T>(
        Guid accountId, Func<IAuditEventRepository, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        return await action(scope.ServiceProvider.GetRequiredService<IAuditEventRepository>());
    }

    // Never changed since creation: the trail's two ends are the SAME event, so
    // there is no change to report. Deciding that here rather than in the UI is
    // deliberate — only this layer can tell one event from two that happen to
    // share an instant (see Provenance_WhenTwoEventsShareAnInstant...).
    [Fact]
    public async Task Provenance_WithOneEvent_ReportsNoChange()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        await SeedEventsAsync(accountId, Event(accountId, entityId, "Flock.Create", "ana@farm.test", 0));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.CreatedByEmail);
        Assert.Equal(Base, provenance.CreatedAtUtc);
        Assert.Null(provenance.LastChangedByEmail);
        Assert.Null(provenance.LastChangedAtUtc);
    }

    // A record that predates #494 has no ".Create" event, only corrections.
    // Naming its first corrector as its author would be a claim the trail does
    // not support — an outright false attribution, not merely missing data. It
    // reports no creator at all, and the column renders blank.
    [Fact]
    public async Task Provenance_ForALegacyRecordWithOnlyCorrections_NamesNoCreator()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "Flock.Update", "bo@farm.test", 30),
            Event(accountId, entityId, "Flock.Archive", "cy@farm.test", 90));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", [entityId]));

        Assert.False(result.ContainsKey(entityId));
    }

    [Fact]
    public async Task Provenance_WithSeveralEvents_ReportsTheEarliestAndTheLatest()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        // Deliberately inserted out of order: the SQL orders, not the insert.
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "Flock.Update", "bo@farm.test", 30),
            Event(accountId, entityId, "Flock.Create", "ana@farm.test", 0),
            Event(accountId, entityId, "Flock.Archive", "cy@farm.test", 90));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.CreatedByEmail);
        Assert.Equal(Base, provenance.CreatedAtUtc);
        Assert.Equal("cy@farm.test", provenance.LastChangedByEmail);
        Assert.Equal(Base.AddMinutes(90), provenance.LastChangedAtUtc);
    }

    // Two events can share an instant, and their relative order is then
    // UNKNOWABLE: AuditWriter mints a random v4 Guid, so an Id tiebreaker sorts
    // arbitrarily rather than chronologically (codex review of PR #503 — an
    // earlier revision of this test picked its expected actor by that ordering
    // and passed only ~half the time).
    //
    // So creation is identified by its ACTION, not by its position in the
    // trail. That is what makes this deterministic: whichever event sorts
    // first, the ".Create" one is the creation and the other is the change.
    [Fact]
    public async Task Provenance_WhenTwoEventsShareAnInstant_NamesTheCreatorByAction()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "Flock.Create", "ana@farm.test", 0),
            Event(accountId, entityId, "Flock.Update", "bo@farm.test", 0));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.CreatedByEmail);
        // Distinct events, so this is a real change and must be reported.
        Assert.NotNull(provenance.LastChangedByEmail);
        Assert.Equal("bo@farm.test", provenance.LastChangedByEmail);
    }

    // --- submitting your own draft is not a change -------------------------
    //
    // Saving a draft and submitting it are two events, but one act: the person
    // wrote the day's numbers down and made them official, changing nothing in
    // between. Reporting "last changed by ana" there is noise that reads like
    // somebody corrected ana's work.
    //
    // So the rule is NOT "the latest event after the creation" — it is "the
    // latest event that is neither the creation NOR a Submit by the creator".
    // Stated as an exclusion rather than as a check on the last event, which is
    // what keeps the three tests below from contradicting each other.

    [Fact]
    public async Task Provenance_WhenTheCreatorSubmitsTheirOwnDraft_ReportsNoChange()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        var ana = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "DailyEntry.Create", "ana@farm.test", 0, "DailyEntry", ana),
            Event(accountId, entityId, "DailyEntry.Submit", "ana@farm.test", 5, "DailyEntry", ana));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("DailyEntry", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.CreatedByEmail);
        Assert.Equal(Base, provenance.CreatedAtUtc);
        Assert.Null(provenance.LastChangedByEmail);
        Assert.Null(provenance.LastChangedAtUtc);
    }

    // The case the whole option exists for: a worker writes the numbers, someone
    // else makes them official. Both people must be visible — that is the
    // accountability the submit step is for.
    [Fact]
    public async Task Provenance_WhenSomeoneElseSubmitsTheDraft_ReportsTheSubmitter()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "DailyEntry.Create", "ana@farm.test", 0, "DailyEntry"),
            Event(accountId, entityId, "DailyEntry.Submit", "bo@farm.test", 5, "DailyEntry"));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("DailyEntry", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.CreatedByEmail);
        Assert.Equal("bo@farm.test", provenance.LastChangedByEmail);
        Assert.Equal(Base.AddMinutes(5), provenance.LastChangedAtUtc);
    }

    // The suppression is keyed on the ACTION, not on the actor alone. Correcting
    // a locked entry is a stock-altering change and must always show, including
    // when the person correcting it is the one who created it — a blanket
    // "same actor" rule would silently swallow exactly that.
    [Fact]
    public async Task Provenance_WhenTheCreatorAdjustsTheirOwnEntry_StillReportsTheChange()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        var ana = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "DailyEntry.Create", "ana@farm.test", 0, "DailyEntry", ana),
            Event(accountId, entityId, "DailyEntry.Submit", "ana@farm.test", 5, "DailyEntry", ana),
            Event(accountId, entityId, "DailyEntry.Adjust", "ana@farm.test", 600, "DailyEntry", ana));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("DailyEntry", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("ana@farm.test", provenance.LastChangedByEmail);
        Assert.Equal(Base.AddMinutes(600), provenance.LastChangedAtUtc);
    }

    // Excluding the self-submit must not take anything else with it. Had the
    // rule been "look at the last event, and if it is a self-submit report
    // nothing", bo's edit would vanish — a real change, hidden. Not reachable
    // through today's handlers (draft re-saves write no event), so this pins the
    // SHAPE of the rule rather than a live path.
    [Fact]
    public async Task Provenance_WhenAnEarlierChangeByAnotherPersonPrecedesASelfSubmit_StillReportsIt()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        var ana = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "DailyEntry.Create", "ana@farm.test", 0, "DailyEntry", ana),
            Event(accountId, entityId, "DailyEntry.Adjust", "bo@farm.test", 2, "DailyEntry"),
            Event(accountId, entityId, "DailyEntry.Submit", "ana@farm.test", 5, "DailyEntry", ana));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("DailyEntry", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("bo@farm.test", provenance.LastChangedByEmail);
        Assert.Equal(Base.AddMinutes(2), provenance.LastChangedAtUtc);
    }

    [Fact]
    public async Task Provenance_IsScopedToTheTenant()
    {
        var mine = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var theirs = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var myEntity = Guid.NewGuid();
        var theirEntity = Guid.NewGuid();
        await SeedEventsAsync(mine, Event(mine, myEntity, "Flock.Create", "ana@farm.test", 0));
        await SeedEventsAsync(theirs, Event(theirs, theirEntity, "Flock.Create", "rival@other.test", 0));

        var result = await WithRepositoryAsync(mine, repo =>
            repo.GetProvenanceAsync("Flock", [myEntity, theirEntity]));

        Assert.True(result.ContainsKey(myEntity));
        Assert.False(result.ContainsKey(theirEntity));
    }

    [Fact]
    public async Task Provenance_IgnoresEventsOfAnotherEntityType()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        // The same id under two entity types: only the asked-for type counts.
        var entityId = Guid.NewGuid();
        await SeedEventsAsync(accountId,
            Event(accountId, entityId, "Expense.Create", "bo@farm.test", 0, entityType: "Expense"));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", [entityId]));

        Assert.Empty(result);
    }

    [Fact]
    public async Task Provenance_ForAnIdWithNoEvents_IsAbsentRatherThanBlank()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var known = Guid.NewGuid();
        var untouched = Guid.NewGuid();
        await SeedEventsAsync(accountId, Event(accountId, known, "Flock.Create", "ana@farm.test", 0));

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", [known, untouched]));

        Assert.True(result.ContainsKey(known));
        Assert.False(result.ContainsKey(untouched));
    }

    [Fact]
    public async Task Provenance_WithNoIds_ReturnsEmptyWithoutQuerying()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", []));

        Assert.Empty(result);
    }

    // --- over the wire -----------------------------------------------------
    //
    // One per extended list endpoint: the point of #494 is that provenance
    // reaches the record's OWN page without a second call, so each of these
    // asserts the creating user's email came back on the list row itself.

    private sealed record ProvenanceRowDto(
        Guid Id, string? CreatedByEmail, DateTimeOffset? CreatedAtUtc,
        string? LastChangedByEmail, DateTimeOffset? LastChangedAtUtc);
    private sealed record ExpenseListDto(List<ProvenanceRowDto> Items);
    private sealed record CreatedDto(Guid Id);

    private async Task<(HttpClient Client, string Email)> AuthedAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        return (factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email)), email);
    }

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedDto>())!.Id;
    }

    private static void AssertCreatedBy(ProvenanceRowDto row, string email)
    {
        Assert.Equal(email, row.CreatedByEmail);
        Assert.NotNull(row.CreatedAtUtc);
        // Never changed since creation: the trail's earliest and latest event
        // are the same one, so the server reports no change at all.
        Assert.Null(row.LastChangedByEmail);
        Assert.Null(row.LastChangedAtUtc);
    }

    [Fact]
    public async Task FlockList_CarriesProvenance()
    {
        var (client, email) = await AuthedAsync();
        var id = await CreatedIdAsync(await client.PostWithKeyAsync(
            "/api/v1/flocks", Guid.NewGuid().ToString(),
            new { name = "Barn A", breed = "ISA Brown", placementDate = "2026-01-01", initialCount = 200 }));

        var rows = await client.GetFromJsonAsync<List<ProvenanceRowDto>>("/api/v1/flocks");

        AssertCreatedBy(rows!.Single(r => r.Id == id), email);
    }

    [Fact]
    public async Task EggGradeList_CarriesProvenance()
    {
        var (client, email) = await AuthedAsync();
        var id = await CreatedIdAsync(await client.PostWithKeyAsync(
            "/api/v1/egg-grades", Guid.NewGuid().ToString(),
            new { name = "Jumbo", gradeType = "Custom", sortOrder = 7, isSaleable = true }));

        var rows = await client.GetFromJsonAsync<List<ProvenanceRowDto>>("/api/v1/egg-grades");

        AssertCreatedBy(rows!.Single(r => r.Id == id), email);
    }

    [Fact]
    public async Task ExpenseList_CarriesProvenance()
    {
        var (client, email) = await AuthedAsync();
        var categoryId = await CreatedIdAsync(await client.PostWithKeyAsync(
            "/api/v1/expense-categories", Guid.NewGuid().ToString(), new { name = "Feed" }));
        var id = await CreatedIdAsync(await client.PostWithKeyAsync(
            "/api/v1/expenses", Guid.NewGuid().ToString(), new
            {
                expenseCategoryId = categoryId,
                date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                description = "Feed delivery",
                amountMinorUnits = 25_00,
            }));

        var list = await client.GetFromJsonAsync<ExpenseListDto>("/api/v1/expenses");

        AssertCreatedBy(list!.Items.Single(r => r.Id == id), email);
    }

    [Fact]
    public async Task SalesOrderList_CarriesProvenance()
    {
        var (client, email) = await AuthedAsync();
        var customerId = await CreatedIdAsync(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Mercado Central", phone = "555-0100" }));
        var id = await CreatedIdAsync(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));

        var rows = await client.GetFromJsonAsync<List<ProvenanceRowDto>>("/api/v1/sales");

        AssertCreatedBy(rows!.Single(r => r.Id == id), email);
    }

    private async Task<(HttpClient Client, string Email, Guid AccountId, Guid EntryId)> DraftDailyEntryAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        // #394: submit requires exact reconciliation, so the draft carries a
        // grade line summing to the total — otherwise every submit here 422s
        // and the tests below pass for the wrong reason.
        var id = await CreatedIdAsync(await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), new
            {
                farmId,
                houseId = Guid.NewGuid(),
                flockId,
                date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                totalEggs = 100,
                crackedEggs = 0,
                dirtyEggs = 0,
                discardedEggs = 0,
                mortalityCount = 0,
                grades = new[] { new { eggGradeId = grades["Large"], quantity = 100 } },
            }));
        return (client, email, accountId, id);
    }

    [Fact]
    public async Task DailyEntryList_CarriesProvenance()
    {
        var (client, email, _, id) = await DraftDailyEntryAsync();

        var rows = await client.GetFromJsonAsync<List<ProvenanceRowDto>>("/api/v1/daily-entries");

        AssertCreatedBy(rows!.Single(r => r.Id == id), email);
    }

    // Submitting is a stock-altering transition (it mints the egg lots), so it
    // belongs in the trail on its own merits. It is also the ONLY source for the
    // "who made this official" half of a daily entry's record history — without
    // this event, a manager submitting a worker's draft leaves no trace at all.
    [Fact]
    public async Task DailyEntrySubmit_WritesAnAuditEvent()
    {
        var (client, _, accountId, id) = await DraftDailyEntryAsync();

        var response = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{id}/submit", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var actions = await factory.WithTenantScopeAsync(accountId, async db =>
            await db.AuditEvents
                .Where(e => e.EntityId == id && e.EntityType == "DailyEntry")
                .Select(e => e.Action)
                .ToListAsync());

        Assert.Contains("DailyEntry.Submit", actions);
    }

    // The end-to-end shape of #494's answer: one person writes the day's numbers
    // and makes them official, having changed nothing in between. Two audit
    // events, one act — the row still reports a creator and NO change.
    //
    // Load-bearing only once the submit event above exists; before that it would
    // pass for the wrong reason. Mutation-checked by removing the self-submit
    // exclusion from AuditEventRepository, which turns this red.
    [Fact]
    public async Task DailyEntryList_WhenTheCreatorSubmitsTheirOwnDraft_ReportsNoChange()
    {
        var (client, email, _, id) = await DraftDailyEntryAsync();
        await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{id}/submit", Guid.NewGuid().ToString());

        var rows = await client.GetFromJsonAsync<List<ProvenanceRowDto>>("/api/v1/daily-entries");

        AssertCreatedBy(rows!.Single(r => r.Id == id), email);
    }

    // Most callers hand this a clamped page, but the egg-grade list has no
    // pagination at all and grades are never deleted — a farm's catalog grows
    // past any fixed cap eventually. So the batch size is an internal chunking
    // detail, NOT a caller contract: more ids than fit in one round trip must
    // still answer correctly rather than fail the whole list endpoint.
    [Fact]
    public async Task Provenance_AboveTheBatchSize_StillResolvesEveryId()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var ids = Enumerable.Range(0, IAuditEventRepository.MaxBatchIds + 25)
            .Select(_ => Guid.NewGuid()).ToArray();
        // Seed an event for every id so a dropped chunk shows up as a missing key.
        await SeedEventsAsync(accountId,
            ids.Select(id => Event(accountId, id, "Flock.Create", "ana@farm.test", 0)).ToArray());

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", ids));

        Assert.Equal(ids.Length, result.Count);
        Assert.All(ids, id => Assert.Equal("ana@farm.test", result[id].CreatedByEmail));
    }
}
