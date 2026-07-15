namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #5: startup seeding makes the app usable end-to-end, and flock CRUD is the
// first setup surface. Covers seeded-admin login + create/list/get/deplete.
[Collection(IntegrationCollection.Name)]
public sealed class SeedAndFlockTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task SeededAdmin_CanLogIn()
    {
        const string email = "admin@cluckwork.local";
        const string password = "SeedPassw0rd!23";

        // A host built with Seed:* config runs the startup seeder against the
        // shared container, creating the default account + admin user.
        using var seeded = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Seed:Enabled", "true");
            builder.UseSetting("Seed:AdminEmail", email);
            builder.UseSetting("Seed:AdminPassword", password);
        });

        var client = seeded.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { email, password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await response.Content.ReadFromJsonAsync<TokenPairDto>();
        Assert.False(string.IsNullOrWhiteSpace(tokens!.AccessToken));
    }

    [Fact]
    public async Task Flock_CreateListGetDeplete_RoundTrips()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var token = await factory.LoginForAccessTokenAsync(email);
        var client = factory.CreateAuthedClient(token);

        // create
        var create = await client.PostWithKeyAsync(
            "/api/v1/flocks", Guid.NewGuid().ToString(),
            new { name = "House 1 layers", breed = "ISA Brown", placementDate = "2026-01-01", initialCount = 500 });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<IdDto>();

        // list
        var list = await client.GetFromJsonAsync<List<FlockDto>>("/api/v1/flocks");
        Assert.Contains(list!, f => f.Id == created!.Id && f.Status == "Active");

        // get
        var got = await client.GetFromJsonAsync<FlockDto>($"/api/v1/flocks/{created!.Id}");
        Assert.Equal("House 1 layers", got!.Name);
        Assert.Equal(500, got.InitialCount);

        // deplete
        var deplete = await client.PostWithKeyAsync(
            $"/api/v1/flocks/{created.Id}/deplete", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, deplete.StatusCode);

        var afterDeplete = await client.GetFromJsonAsync<FlockDto>($"/api/v1/flocks/{created.Id}");
        Assert.Equal("Depleted", afterDeplete!.Status);
    }

    [Fact]
    public async Task Flock_List_IsTenantScoped()
    {
        // A flock created by one account is not visible to another.
        var emailA = $"a-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(emailA);
        var clientA = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(emailA));
        var create = await clientA.PostWithKeyAsync(
            "/api/v1/flocks", Guid.NewGuid().ToString(),
            new { name = "A-only", breed = "ISA Brown", placementDate = "2026-01-01", initialCount = 100 });
        var created = await create.Content.ReadFromJsonAsync<IdDto>();

        var emailB = $"b-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(emailB);
        var clientB = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(emailB));

        var listB = await clientB.GetFromJsonAsync<List<FlockDto>>("/api/v1/flocks");
        Assert.DoesNotContain(listB!, f => f.Id == created!.Id);

        var getB = await clientB.GetAsync($"/api/v1/flocks/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getB.StatusCode);
    }

    private sealed record IdDto(Guid Id);

    private sealed record FlockDto(
        Guid Id, Guid FarmId, Guid HouseId, string Name, string Breed,
        DateOnly PlacementDate, int InitialCount, string Status);
}
