namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #746 — CreateProductHandler and UpdateProductHandler stored ProductType and
// DefaultUnit as bare enums in their audit payloads. AuditWriter serialises
// `details` with no JsonStringEnumConverter, so a bare enum stores as its
// underlying ordinal — meaningful only against the member order at write
// time, and silently re-read as a different member after any reorder.
[Collection(IntegrationCollection.Name)]
public sealed class ProductAuditPayloadTests(CluckworkWebApplicationFactory factory)
{
    private sealed record IdDto(Guid Id);
    private sealed record AuditRow(Guid Id, string Action, Guid EntityId, string? DetailsJson);

    private async Task<(HttpClient Client, Guid AccountId, Guid EggGradeId)> SetupAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, accountId, grades["Large"]);
    }

    private async Task<Guid> CreatedId(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    private async Task<AuditRow> SingleAuditRowAsync(HttpClient client, string action, Guid entityId)
    {
        var rows = await client.GetFromJsonAsync<List<AuditRow>>(
            $"/api/v1/audit?action={action}&entityId={entityId}");
        return Assert.Single(rows!);
    }

    [Fact]
    public async Task CreateProduct_RecordsProductTypeByName()
    {
        var (client, _, eggGradeId) = await SetupAsync();

        var productId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/products", Guid.NewGuid().ToString(),
            new
            {
                name = "Large Eggs",
                productType = "Egg",
                defaultUnit = "Egg",
                defaultPriceMinorUnits = 500,
                eggGradeId,
            }));

        var row = await SingleAuditRowAsync(client, "Product.Create", productId);
        using var details = JsonDocument.Parse(row.DetailsJson!);
        var root = details.RootElement;

        // The NAME, never the ordinal (Egg is ordinal 0, so a stored 0 is
        // invisible to a lazy assertion). AuditWriter registers no
        // JsonStringEnumConverter, so a bare enum serialises as its
        // underlying integer here; GetString() throws
        // InvalidOperationException on a JSON number, which is why this
        // fails LOUDLY instead of pinning a number that silently re-reads as
        // a different member after ProductType is reordered.
        Assert.Equal("Egg", root.GetProperty("productType").GetString());
        Assert.Equal("Large Eggs", root.GetProperty("name").GetString());
        Assert.Equal("Large", root.GetProperty("eggGrade").GetString());
    }

    [Fact]
    public async Task UpdateProduct_RecordsDefaultUnitByName()
    {
        var (client, _, eggGradeId) = await SetupAsync();

        var productId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/products", Guid.NewGuid().ToString(),
            new
            {
                name = "Large Eggs",
                productType = "Egg",
                defaultUnit = "Egg",
                defaultPriceMinorUnits = 500,
                eggGradeId,
            }));

        var updated = await client.PutWithKeyAsync(
            $"/api/v1/products/{productId}", Guid.NewGuid().ToString(),
            new
            {
                name = "Large Eggs Packed",
                defaultUnit = "Tray",
                defaultPriceMinorUnits = 650,
                eggGradeId,
            });
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);

        var row = await SingleAuditRowAsync(client, "Product.Update", productId);
        using var details = JsonDocument.Parse(row.DetailsJson!);
        var root = details.RootElement;

        // The NAME, never the ordinal (Tray is ordinal 3 — distinct from
        // Egg's 0, so a transposition of these two fields reddens too).
        Assert.Equal("Tray", root.GetProperty("defaultUnit").GetString());
        Assert.Equal("Large Eggs Packed", root.GetProperty("name").GetString());
        Assert.Equal(650, root.GetProperty("defaultPriceMinorUnits").GetInt64());
        Assert.Equal("Large", root.GetProperty("eggGrade").GetString());
    }
}
