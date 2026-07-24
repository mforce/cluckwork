namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #163 — a user's display name can be set at creation and edited afterwards.
// The update is Owner-only (covered by AdminGatingTests) and account-scoped.
[Collection(IntegrationCollection.Name)]
public sealed class UserNameTests(CluckworkWebApplicationFactory factory)
{
    private sealed record UserRow(Guid Id, string Email, string? DisplayName, string Role);

    private async Task<(HttpClient Admin, Guid AccountId)> AdminAsync()
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var admin = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (admin, accountId);
    }

    private static async Task<UserRow> FindUserAsync(HttpClient admin, string email)
    {
        var users = await admin.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        return users!.Single(u => u.Email == email);
    }

    [Fact]
    public async Task Create_WithName_PersistsTheDisplayName()
    {
        var (admin, _) = await AdminAsync();
        var email = $"hand-{Guid.NewGuid():N}@test.local";

        var created = await admin.PostWithKeyAsync("/api/v1/users", Guid.NewGuid().ToString(),
            new { email, password = TestHarness.Password, role = "Worker", name = "  Ada Lovelace  " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var row = await FindUserAsync(admin, email);
        Assert.Equal("Ada Lovelace", row.DisplayName); // trimmed
    }

    [Fact]
    public async Task Create_WithoutName_LeavesDisplayNameNull()
    {
        var (admin, _) = await AdminAsync();
        var email = $"hand-{Guid.NewGuid():N}@test.local";

        await admin.PostWithKeyAsync("/api/v1/users", Guid.NewGuid().ToString(),
            new { email, password = TestHarness.Password, role = "Worker" });

        Assert.Null((await FindUserAsync(admin, email)).DisplayName);
    }

    [Fact]
    public async Task Update_SetsAndThenClearsTheDisplayName()
    {
        var (admin, _) = await AdminAsync();
        var email = $"hand-{Guid.NewGuid():N}@test.local";
        await admin.PostWithKeyAsync("/api/v1/users", Guid.NewGuid().ToString(),
            new { email, password = TestHarness.Password, role = "Worker" });
        var user = await FindUserAsync(admin, email);

        // Set a name.
        var set = await admin.PutWithKeyAsync($"/api/v1/users/{user.Id}", Guid.NewGuid().ToString(),
            new { name = "Grace Hopper" });
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        Assert.Equal("Grace Hopper", (await FindUserAsync(admin, email)).DisplayName);

        // Clear it (blank → null → "—").
        var cleared = await admin.PutWithKeyAsync($"/api/v1/users/{user.Id}", Guid.NewGuid().ToString(),
            new { name = "   " });
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.Null((await FindUserAsync(admin, email)).DisplayName);
    }

    [Fact]
    public async Task Update_UnknownUser_Is404()
    {
        var (admin, _) = await AdminAsync();
        var response = await admin.PutWithKeyAsync($"/api/v1/users/{Guid.NewGuid()}",
            Guid.NewGuid().ToString(), new { name = "Nobody" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_UserInAnotherAccount_Is404_NotACrossTenantEdit()
    {
        // A user that belongs to a DIFFERENT account.
        var foreignEmail = $"foreign-{Guid.NewGuid():N}@test.local";
        var foreignAccount = await factory.SeedAccountWithUserAsync(foreignEmail);
        var foreignAdmin = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(foreignEmail));
        var foreignUser = await FindUserAsync(foreignAdmin, foreignEmail);

        var (admin, _) = await AdminAsync();
        var response = await admin.PutWithKeyAsync($"/api/v1/users/{foreignUser.Id}",
            Guid.NewGuid().ToString(), new { name = "Hijacked" });

        // Account-scoped lookup → the foreign id simply isn't found for this admin.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(foreignEmail, (await FindUserAsync(foreignAdmin, foreignEmail)).Email);
        Assert.Null((await FindUserAsync(foreignAdmin, foreignEmail)).DisplayName); // untouched
    }

    [Fact]
    public async Task Update_NameTooLong_Is400()
    {
        var (admin, _) = await AdminAsync();
        var email = $"hand-{Guid.NewGuid():N}@test.local";
        await admin.PostWithKeyAsync("/api/v1/users", Guid.NewGuid().ToString(),
            new { email, password = TestHarness.Password, role = "Worker" });
        var user = await FindUserAsync(admin, email);

        var response = await admin.PutWithKeyAsync($"/api/v1/users/{user.Id}",
            Guid.NewGuid().ToString(), new { name = new string('x', 129) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
