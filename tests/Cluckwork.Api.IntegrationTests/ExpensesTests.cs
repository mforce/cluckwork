namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// #87 — basic expenses: farm-scoped category catalog (grade pattern) and
// money-out records with a snapshotted currency, server-side period totals,
// and version-guarded admin corrections.
[Collection(IntegrationCollection.Name)]
public sealed class ExpensesTests(CluckworkWebApplicationFactory factory)
{
    private sealed record Created(Guid Id);
    private sealed record CategoryDto(Guid Id, Guid FarmId, string Name, bool Active);
    private sealed record ExpenseDto(
        Guid Id, Guid FarmId, Guid ExpenseCategoryId, DateOnly Date, string Description,
        long AmountMinorUnits, string CurrencyCode, int CurrencyMinorUnit,
        Guid? FlockId, string? Note, int Version);
    private sealed record ListDto(
        List<ExpenseDto> Items, long TotalMinorUnits, string CurrencyCode, int CurrencyMinorUnit);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private async Task<HttpClient> AdminAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        return factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
    }

    private static async Task<Guid> CreateCategoryAsync(HttpClient client, string name)
    {
        var response = await client.PostWithKeyAsync(
            "/api/v1/expense-categories", Guid.NewGuid().ToString(), new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Created>())!.Id;
    }

    private static async Task<Guid> CreateExpenseAsync(
        HttpClient client, Guid categoryId, long amount, DateOnly? date = null,
        string description = "Feed delivery", Guid? flockId = null, string? note = null)
    {
        var response = await client.PostWithKeyAsync(
            "/api/v1/expenses", Guid.NewGuid().ToString(), new
            {
                expenseCategoryId = categoryId,
                date = date ?? Today,
                description,
                amountMinorUnits = amount,
                flockId,
                note
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Created>())!.Id;
    }

    private static Task<HttpResponseMessage> AdjustAsync(HttpClient client, Guid id, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/expenses/{id}")
        { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Categories_CreateListDuplicateDeactivate_FullLoop()
    {
        var client = await AdminAsync();
        var feedId = await CreateCategoryAsync(client, "Feed");

        // Case-insensitive duplicate → 409 (precheck; lower(Name) index backstops).
        var dup = await client.PostWithKeyAsync(
            "/api/v1/expense-categories", Guid.NewGuid().ToString(), new { name = "  feed " });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        await CreateCategoryAsync(client, "Vet");

        var active = await client.GetFromJsonAsync<List<CategoryDto>>("/api/v1/expense-categories");
        Assert.Equal(2, active!.Count);

        // Deactivate Feed: gone from the active list, still in the management view.
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/expense-categories/{feedId}")
        { Content = JsonContent.Create(new { name = "Feed", active = false }) };
        put.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(put)).StatusCode);

        active = await client.GetFromJsonAsync<List<CategoryDto>>("/api/v1/expense-categories");
        Assert.Single(active!);
        Assert.Equal("Vet", active![0].Name);

        var all = await client.GetFromJsonAsync<List<CategoryDto>>(
            "/api/v1/expense-categories?includeInactive=true");
        Assert.Equal(2, all!.Count);

        // New expenses refuse the deactivated category.
        var refused = await client.PostWithKeyAsync(
            "/api/v1/expenses", Guid.NewGuid().ToString(), new
            {
                expenseCategoryId = feedId,
                date = Today,
                description = "late feed bill",
                amountMinorUnits = 100
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    [Fact]
    public async Task Expenses_RecordListTotal_CurrencySnapshotAndFilters()
    {
        var client = await AdminAsync();
        var feed = await CreateCategoryAsync(client, "Feed");
        var vet = await CreateCategoryAsync(client, "Vet");

        await CreateExpenseAsync(client, feed, 12_50);
        await CreateExpenseAsync(client, feed, 7_50, Today.AddDays(-1));
        await CreateExpenseAsync(client, vet, 100_00, note: "  annual check  ");

        var list = await client.GetFromJsonAsync<ListDto>("/api/v1/expenses");
        Assert.Equal(3, list!.Items.Count);
        Assert.Equal(120_00, list.TotalMinorUnits);
        Assert.All(list.Items, x => Assert.Equal(list.CurrencyCode, x.CurrencyCode));
        Assert.Equal("annual check", list.Items.Single(x => x.ExpenseCategoryId == vet).Note);

        // Category filter narrows the total, not just the page.
        var feedOnly = await client.GetFromJsonAsync<ListDto>($"/api/v1/expenses?categoryId={feed}");
        Assert.Equal(2, feedOnly!.Items.Count);
        Assert.Equal(20_00, feedOnly.TotalMinorUnits);

        // Date filter: yesterday only.
        var yesterday = await client.GetFromJsonAsync<ListDto>(
            $"/api/v1/expenses?from={Today.AddDays(-1):yyyy-MM-dd}&to={Today.AddDays(-1):yyyy-MM-dd}");
        Assert.Single(yesterday!.Items);
        Assert.Equal(7_50, yesterday.TotalMinorUnits);

        // Future dates are refused up front.
        var future = await client.PostWithKeyAsync(
            "/api/v1/expenses", Guid.NewGuid().ToString(), new
            {
                expenseCategoryId = feed,
                date = Today.AddDays(2),
                description = "prepaid",
                amountMinorUnits = 100
            });
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
    }

    [Fact]
    public async Task Adjust_CorrectsInPlace_VersionGuards_GrandfathersCategory()
    {
        var client = await AdminAsync();
        var feed = await CreateCategoryAsync(client, "Feed");
        var vet = await CreateCategoryAsync(client, "Vet");
        var id = await CreateExpenseAsync(client, feed, 50_00);

        // Happy path: returns the corrected row with the bumped version.
        var ok = await AdjustAsync(client, id, new
        {
            version = 0,
            expenseCategoryId = vet,
            date = Today.AddDays(-2),
            description = "actually the vet",
            amountMinorUnits = 55_00,
            note = "recategorized"
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var updated = await ok.Content.ReadFromJsonAsync<ExpenseDto>();
        Assert.Equal(1, updated!.Version);
        Assert.Equal(vet, updated.ExpenseCategoryId);
        Assert.Equal(55_00, updated.AmountMinorUnits);

        // Stale base version → deterministic 409.
        var stale = await AdjustAsync(client, id, new
        {
            version = 0,
            expenseCategoryId = vet,
            date = Today,
            description = "stale",
            amountMinorUnits = 1_00
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // Deactivate vet: KEEPING it on a correction is legal (grandfathering)...
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/expense-categories/{vet}")
        { Content = JsonContent.Create(new { name = "Vet", active = false }) };
        put.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(put)).StatusCode);

        var keep = await AdjustAsync(client, id, new
        {
            version = 1,
            expenseCategoryId = vet,
            date = Today.AddDays(-2),
            description = "still the vet",
            amountMinorUnits = 55_00
        });
        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);

        // ...but RETARGETING another expense onto it is refused.
        var other = await CreateExpenseAsync(client, feed, 10_00);
        var retarget = await AdjustAsync(client, other, new
        {
            version = 0,
            expenseCategoryId = vet,
            date = Today,
            description = "sneaky",
            amountMinorUnits = 10_00
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, retarget.StatusCode);

        // Currency never changed across corrections.
        var final = await client.GetFromJsonAsync<ExpenseDto>($"/api/v1/expenses/{id}");
        Assert.Equal(updated.CurrencyCode, final!.CurrencyCode);
    }

    // AGENTS.md race rule: same base version, exactly one wins, Version delta 1.
    [Fact]
    public async Task ParallelAdjusts_SameBaseVersion_ExactlyOneWins()
    {
        var client = await AdminAsync();
        var feed = await CreateCategoryAsync(client, "Feed");
        var id = await CreateExpenseAsync(client, feed, 50_00);

        object Body(long amount, string description) => new
        {
            version = 0,
            expenseCategoryId = feed,
            date = Today,
            description,
            amountMinorUnits = amount
        };

        var responses = await Task.WhenAll(
            AdjustAsync(client, id, Body(60_00, "first")),
            AdjustAsync(client, id, Body(40_00, "second")));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var after = await client.GetFromJsonAsync<ExpenseDto>($"/api/v1/expenses/{id}");
        Assert.Equal(1, after!.Version);
        Assert.True(after.AmountMinorUnits is 60_00 or 40_00);
        Assert.Equal(after.AmountMinorUnits == 60_00 ? "first" : "second", after.Description);
    }

    // #494 — creation wasn't on the audit trail at all; only corrections were.
    [Fact]
    public async Task Expense_Create_WritesAuditEvent()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var categoryId = await CreateCategoryAsync(client, "Feed");
        var id = await CreateExpenseAsync(client, categoryId, 25_00);

        var events = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(e => e.EntityType == "Expense" && e.EntityId == id)
            .ToListAsync());

        var created = Assert.Single(events);
        Assert.Equal("Expense.Create", created.Action);
    }
}
