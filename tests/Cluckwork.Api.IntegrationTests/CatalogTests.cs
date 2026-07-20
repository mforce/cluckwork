namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;

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
                name = "Best Eggs", defaultUnit = "Carton",
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
        { Content = JsonContent.Create(new { eggsPerUnit = 30, active = true }) };
        put.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(put)).StatusCode);

        var updated = await client.GetFromJsonAsync<List<ConversionRow>>("/api/v1/egg-unit-conversions");
        Assert.Equal(30, updated!.Single(c => c.UnitCode == "Carton").EggsPerUnit);

        // Individual is immutable → 422; eggsPerUnit < 1 → 400.
        var individual = conversions.Single(c => c.UnitCode == "Individual");
        var immutable = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/egg-unit-conversions/{individual.Id}")
        { Content = JsonContent.Create(new { eggsPerUnit = 6, active = true }) };
        immutable.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.SendAsync(immutable)).StatusCode);

        var zero = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/egg-unit-conversions/{carton.Id}")
        { Content = JsonContent.Create(new { eggsPerUnit = 0, active = true }) };
        zero.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(zero)).StatusCode);
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
        { Content = JsonContent.Create(new { eggsPerUnit = 24, active = true }) };
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
