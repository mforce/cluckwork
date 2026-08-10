namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.Audit;
using Cluckwork.Domain.Auditing;
using Cluckwork.Infrastructure.Persistence;
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

    private static AuditEvent Event(
        Guid accountId, Guid entityId, string action, string email, int minutesFromBase,
        string entityType = "Flock") =>
        AuditEvent.Create(
            Guid.NewGuid(), accountId, Base.AddMinutes(minutesFromBase),
            Guid.NewGuid(), email, action, entityType, entityId);

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

    // Two events can share an instant — the queries carry an Id tiebreaker
    // precisely because of it, and a seeder writing off a fixed clock produces
    // it readily. The change must still be reported: a caller that told them
    // apart only by timestamp would call this record untouched since creation.
    [Fact]
    public async Task Provenance_WhenTwoEventsShareAnInstant_StillReportsTheChange()
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

    [Fact]
    public async Task DailyEntryList_CarriesProvenance()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

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
            }));

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
