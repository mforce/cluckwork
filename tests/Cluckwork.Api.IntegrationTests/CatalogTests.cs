using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Api.IntegrationTests;

// #97 (part 1) — product catalog: egg products mapped to grades, packed-unit
// conversions, admin-gated writes, seeded defaults, audit trail.
[Collection(IntegrationCollection.Name)]
public sealed class CatalogTests(CluckworkWebApplicationFactory factory)
{
    private sealed record Created(Guid Id);
    private sealed record ProductRow(
        Guid Id, string Name, string ProductType, string DefaultUnit,
        long? DefaultPriceMinorUnits, string CurrencyCode, int CurrencyMinorUnit,
        Guid? EggGradeId, string? Notes, bool Active, int Version);
    private sealed record ConversionRow(Guid Id, string UnitCode, int EggsPerUnit, bool Active, int Version);
    private sealed record AuditRow(Guid Id, string Action, Guid EntityId, string? DetailsJson);

    private async Task<(HttpClient Client, Guid AccountId, Guid FarmId, Dictionary<string, Guid> Grades)>
        SetupAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large", "Medium");
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, accountId, farmId, grades);
    }

    private static Task<HttpResponseMessage> CreateProductAsync(
        HttpClient client, string name, Guid gradeId, string unit = "Dozen",
        long? price = 450, string type = "Egg") =>
        client.PostWithKeyAsync("/api/v1/products", Guid.NewGuid().ToString(), new
        {
            name, productType = type, defaultUnit = unit,
            defaultPriceMinorUnits = price, eggGradeId = gradeId, notes = (string?)null
        });

    [Fact]
    public async Task Create_ListsWithMapping_CurrencySnapshot_DuplicateNameConflicts()
    {
        var (client, _, _, grades) = await SetupAsync();

        var created = await CreateProductAsync(client, "Large Eggs", grades["Large"]);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var list = await client.GetFromJsonAsync<List<ProductRow>>("/api/v1/products");
        var product = Assert.Single(list!);
        Assert.Equal("Large Eggs", product.Name);
        Assert.Equal("Egg", product.ProductType);
        Assert.Equal("Dozen", product.DefaultUnit);
        Assert.Equal(450, product.DefaultPriceMinorUnits);
        Assert.Equal(grades["Large"], product.EggGradeId);
        // Currency snapshotted from the account (spec §16).
        Assert.Equal("USD", product.CurrencyCode);
        Assert.Equal(2, product.CurrencyMinorUnit);

        // Case-insensitive duplicate → 409.
        var dup = await CreateProductAsync(client, "large eggs", grades["Medium"]);
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Create_RejectsNonEggTypes_MissingGrade_InactiveGrade()
    {
        var (client, _, _, grades) = await SetupAsync();

        // Only egg products in this phase → validator 400.
        var service = await CreateProductAsync(client, "Delivery fee", grades["Large"], type: "Service");
        Assert.Equal(HttpStatusCode.BadRequest, service.StatusCode);

        // Unknown grade → 422.
        var unknown = await CreateProductAsync(client, "Ghost", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        // Inactive grade → 422.
        var deact = await client.PostWithKeyAsync(
            $"/api/v1/egg-grades/{grades["Medium"]}/deactivate", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, deact.StatusCode);
        var inactive = await CreateProductAsync(client, "Medium Eggs", grades["Medium"]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inactive.StatusCode);
    }

    [Fact]
    public async Task Update_RepointsGrade_DeactivateHidesFromDefaultList()
    {
        var (client, _, _, grades) = await SetupAsync();
        var created = await CreateProductAsync(client, "Eggs", grades["Large"]);
        var id = (await created.Content.ReadFromJsonAsync<Created>())!.Id;

        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/products/{id}")
        {
            Content = JsonContent.Create(new
            {
                version = 0, name = "Best Eggs", defaultUnit = "Carton",
                defaultPriceMinorUnits = (long?)600, eggGradeId = grades["Medium"],
                notes = "premium",
            })
        };
        put.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(put)).StatusCode);

        var row = Assert.Single((await client.GetFromJsonAsync<List<ProductRow>>("/api/v1/products"))!);
        Assert.Equal("Best Eggs", row.Name);
        Assert.Equal("Carton", row.DefaultUnit);
        Assert.Equal(grades["Medium"], row.EggGradeId);
        Assert.Equal(1, row.Version);

        // Deactivate: gone from the default list, still present with includeInactive.
        await client.PostWithKeyAsync($"/api/v1/products/{id}/deactivate", Guid.NewGuid().ToString());
        Assert.Empty((await client.GetFromJsonAsync<List<ProductRow>>("/api/v1/products"))!);
        var all = Assert.Single((await client.GetFromJsonAsync<List<ProductRow>>(
            "/api/v1/products?includeInactive=true"))!);
        Assert.False(all.Active);

        // Double-deactivate → 409.
        var again = await client.PostWithKeyAsync(
            $"/api/v1/products/{id}/deactivate", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Conversions_SeededDefaults_UpdateGuards_IndividualImmutable()
    {
        var (client, _, _, _) = await SetupAsync();

        var conversions = await client.GetFromJsonAsync<List<ConversionRow>>("/api/v1/egg-unit-conversions");
        Assert.Equal(6, conversions!.Count);
        Assert.Equal(1, conversions.Single(c => c.UnitCode == "Individual").EggsPerUnit);
        Assert.Equal(12, conversions.Single(c => c.UnitCode == "Dozen").EggsPerUnit);
        Assert.Equal(30, conversions.Single(c => c.UnitCode == "Tray").EggsPerUnit);
        Assert.Equal(360, conversions.Single(c => c.UnitCode == "Case").EggsPerUnit);

        // A market where the carton is 30 eggs.
        var carton = conversions.Single(c => c.UnitCode == "Carton");
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/egg-unit-conversions/{carton.Id}")
        { Content = JsonContent.Create(new { version = carton.Version, eggsPerUnit = 30, active = true }) };
        put.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(put)).StatusCode);

        var updated = await client.GetFromJsonAsync<List<ConversionRow>>("/api/v1/egg-unit-conversions");
        Assert.Equal(30, updated!.Single(c => c.UnitCode == "Carton").EggsPerUnit);

        // Individual is immutable → 422; eggsPerUnit < 1 → 400.
        var individual = conversions.Single(c => c.UnitCode == "Individual");
        var immutable = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/egg-unit-conversions/{individual.Id}")
        { Content = JsonContent.Create(new { version = individual.Version, eggsPerUnit = 6, active = true }) };
        immutable.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.SendAsync(immutable)).StatusCode);

        var zero = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/egg-unit-conversions/{carton.Id}")
        { Content = JsonContent.Create(new { version = carton.Version + 1, eggsPerUnit = 0, active = true }) };
        zero.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(zero)).StatusCode);
    }

    [Fact]
    public async Task Create_RejectsNonEggUnits_AndNumericEnumValues()
    {
        var (client, _, _, grades) = await SetupAsync();

        // Units that can't resolve through EggUnitConversion → 400.
        foreach (var unit in new[] { "Bird", "Kg", "Package", "Other" })
            Assert.Equal(HttpStatusCode.BadRequest,
                (await CreateProductAsync(client, $"P-{unit}", grades["Large"], unit: unit)).StatusCode);

        // Enum.TryParse accepts bare numerals — the validator must not.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await CreateProductAsync(client, "P-num", grades["Large"], unit: "999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await CreateProductAsync(client, "P-numtype", grades["Large"], type: "1")).StatusCode);
    }

    // AGENTS.md race rule: two admins editing from the same loaded Version.
    // Exactly one wins, the other gets 409, and the stored row is wholly the
    // winner's payload — never last-writer-wins.
    [Fact]
    public async Task Product_Update_SameBaseVersion_ExactlyOneWinner()
    {
        var (client, accountId, _, grades) = await SetupAsync();
        var created = await CreateProductAsync(client, "Race Eggs", grades["Large"]);
        var id = (await created.Content.ReadFromJsonAsync<Created>())!.Id;

        Task<HttpResponseMessage> Update(string name, long price) =>
            client.PutWithKeyAsync($"/api/v1/products/{id}", Guid.NewGuid().ToString(), new
            {
                version = 0, name, defaultUnit = "Dozen",
                defaultPriceMinorUnits = (long?)price, eggGradeId = grades["Large"],
                notes = (string?)null,
            });

        var responses = await Task.WhenAll(Update("Race Eggs A", 500), Update("Race Eggs B", 900));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);

        var row = await factory.WithTenantScopeAsync(accountId, db => db.Products.SingleAsync(p => p.Id == id));
        Assert.Equal(1, row.Version);
        Assert.True(
            (row.Name == "Race Eggs A" && row.DefaultPriceMinorUnits == 500) ||
            (row.Name == "Race Eggs B" && row.DefaultPriceMinorUnits == 900),
            "final row must be wholly one winner's payload");
    }

    [Fact]
    public async Task Conversion_Update_SameBaseVersion_ExactlyOneWinner()
    {
        var (client, accountId, _, _) = await SetupAsync();
        var carton = (await client.GetFromJsonAsync<List<ConversionRow>>("/api/v1/egg-unit-conversions"))!
            .Single(c => c.UnitCode == "Carton");

        Task<HttpResponseMessage> Update(int eggs) =>
            client.PutWithKeyAsync($"/api/v1/egg-unit-conversions/{carton.Id}", Guid.NewGuid().ToString(),
                new { version = carton.Version, eggsPerUnit = eggs, active = true });

        var responses = await Task.WhenAll(Update(18), Update(30));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);

        var row = await factory.WithTenantScopeAsync(accountId,
            db => db.EggUnitConversions.SingleAsync(c => c.Id == carton.Id));
        Assert.Equal(carton.Version + 1, row.Version);
        Assert.Contains(row.EggsPerUnit, new[] { 18, 30 });
    }

    // The deterministic half: a form loaded before someone else's save is
    // refused even when the two requests never overlap.
    [Fact]
    public async Task Product_StaleBaseVersion_Is409_AndChangesNothing()
    {
        var (client, accountId, _, grades) = await SetupAsync();
        var created = await CreateProductAsync(client, "Stale Eggs", grades["Large"]);
        var id = (await created.Content.ReadFromJsonAsync<Created>())!.Id;
        object Body(string name) => new
        {
            version = 0, name, defaultUnit = "Dozen",
            defaultPriceMinorUnits = (long?)500, eggGradeId = grades["Large"], notes = (string?)null,
        };

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutWithKeyAsync($"/api/v1/products/{id}", Guid.NewGuid().ToString(), Body("First"))).StatusCode);
        var stale = await client.PutWithKeyAsync($"/api/v1/products/{id}", Guid.NewGuid().ToString(), Body("Second"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("Product.VersionMismatch", await stale.Content.ReadAsStringAsync());

        var product = await factory.WithTenantScopeAsync(accountId, db => db.Products.SingleAsync(p => p.Id == id));
        Assert.Equal(("First", 1), (product.Name, product.Version));
    }

    [Fact]
    public async Task Conversion_StaleBaseVersion_Is409_AndChangesNothing()
    {
        var (client, accountId, _, _) = await SetupAsync();
        var carton = (await client.GetFromJsonAsync<List<ConversionRow>>("/api/v1/egg-unit-conversions"))!
            .Single(c => c.UnitCode == "Carton");
        object Conv(int eggs) => new { version = carton.Version, eggsPerUnit = eggs, active = true };
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutWithKeyAsync(
            $"/api/v1/egg-unit-conversions/{carton.Id}", Guid.NewGuid().ToString(), Conv(18))).StatusCode);
        var staleConv = await client.PutWithKeyAsync(
            $"/api/v1/egg-unit-conversions/{carton.Id}", Guid.NewGuid().ToString(), Conv(30));
        Assert.Equal(HttpStatusCode.Conflict, staleConv.StatusCode);
        Assert.Contains("EggUnitConversion.VersionMismatch", await staleConv.Content.ReadAsStringAsync());

        var conversion = await factory.WithTenantScopeAsync(accountId,
            db => db.EggUnitConversions.SingleAsync(c => c.Id == carton.Id));
        Assert.Equal((18, carton.Version + 1), (conversion.EggsPerUnit, conversion.Version));
    }

    // An omitted version is a 400, not a silent pass as version 0.
    [Fact]
    public async Task MissingVersion_Is400()
    {
        var (client, _, _, grades) = await SetupAsync();
        var created = await CreateProductAsync(client, "No Version", grades["Large"]);
        var id = (await created.Content.ReadFromJsonAsync<Created>())!.Id;
        var carton = (await client.GetFromJsonAsync<List<ConversionRow>>("/api/v1/egg-unit-conversions"))!
            .Single(c => c.UnitCode == "Carton");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutWithKeyAsync(
            $"/api/v1/products/{id}", Guid.NewGuid().ToString(), new
            {
                name = "X", defaultUnit = "Dozen", defaultPriceMinorUnits = (long?)500,
                eggGradeId = grades["Large"], notes = (string?)null,
            })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutWithKeyAsync(
            $"/api/v1/egg-unit-conversions/{carton.Id}", Guid.NewGuid().ToString(),
            new { eggsPerUnit = 18, active = true })).StatusCode);
    }

    // The lower(Name) unique index is the real duplicate guarantee — the
    // repository pre-check alone can't stop simultaneous requests.
    [Fact]
    public async Task ParallelSameNameCreates_ExactlyOneWins()
    {
        var (client, _, _, grades) = await SetupAsync();

        var responses = await Task.WhenAll(
            CreateProductAsync(client, "Race Dozen", grades["Large"]),
            CreateProductAsync(client, "race dozen", grades["Medium"]));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Single((await client.GetFromJsonAsync<List<ProductRow>>(
            "/api/v1/products?includeInactive=true"))!);
    }

    [Fact]
    public async Task Writes_AreAdminOnly_ReadsOpen()
    {
        var (admin, accountId, _, grades) = await SetupAsync();
        var created = await CreateProductAsync(admin, "Eggs", grades["Large"]);
        var id = (await created.Content.ReadFromJsonAsync<Created>())!.Id;
        var conversions = await admin.GetFromJsonAsync<List<ConversionRow>>("/api/v1/egg-unit-conversions");

        var workerEmail = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, asAdmin: false);
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));

        // Reads open (part 2's sales screens need them).
        Assert.Equal(HttpStatusCode.OK, (await worker.GetAsync("/api/v1/products")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await worker.GetAsync("/api/v1/egg-unit-conversions")).StatusCode);

        // Writes 403.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await CreateProductAsync(worker, "Nope", grades["Large"])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await worker.PostWithKeyAsync($"/api/v1/products/{id}/deactivate", Guid.NewGuid().ToString())).StatusCode);
        var put = new HttpRequestMessage(HttpMethod.Put,
            $"/api/v1/egg-unit-conversions/{conversions![0].Id}")
        { Content = JsonContent.Create(new { version = conversions[0].Version, eggsPerUnit = 24, active = true }) };
        put.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.SendAsync(put)).StatusCode);
    }

    [Fact]
    public async Task Catalog_IsTenantIsolated_AndAudited()
    {
        var (clientA, _, _, gradesA) = await SetupAsync();
        var created = await CreateProductAsync(clientA, "Tenant A Eggs", gradesA["Large"]);
        var productId = (await created.Content.ReadFromJsonAsync<Created>())!.Id;

        // Audit trail carries the creation.
        var audits = await clientA.GetFromJsonAsync<List<AuditRow>>(
            $"/api/v1/audit?action=Product.Create&entityId={productId}");
        var row = Assert.Single(audits!);
        Assert.Contains("Tenant A Eggs", row.DetailsJson);

        // Tenant B sees neither products nor A's conversions; same-name create succeeds.
        var (clientB, _, _, gradesB) = await SetupAsync();
        Assert.Empty((await clientB.GetFromJsonAsync<List<ProductRow>>(
            "/api/v1/products?includeInactive=true"))!);
        Assert.Equal(6, (await clientB.GetFromJsonAsync<List<ConversionRow>>(
            "/api/v1/egg-unit-conversions"))!.Count);
        Assert.Equal(HttpStatusCode.Created,
            (await CreateProductAsync(clientB, "Tenant A Eggs", gradesB["Large"])).StatusCode);
    }
}
