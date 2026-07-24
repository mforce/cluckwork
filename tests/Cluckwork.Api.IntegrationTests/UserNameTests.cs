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

    // The group is Owner-only, NOT Admin-only: a Manager must be refused. Guards
    // against an accidental OwnerOnly -> AdminOnly slip that a Worker-only gating
    // test wouldn't catch (#163 review).
    [Fact]
    public async Task Update_AsManager_Is403_AndLeavesTheTargetUnchanged()
    {
        var adminEmail = $"admin-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(adminEmail);
        var managerEmail = $"mgr-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, managerEmail, role: "Manager");
        var admin = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(adminEmail));
        var manager = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(managerEmail));

        var admin2 = await admin.PutWithKeyAsync(
            $"/api/v1/users/{(await FindUserAsync(admin, managerEmail)).Id}",
            Guid.NewGuid().ToString(), new { name = "Set by owner" });
        Assert.Equal(HttpStatusCode.NoContent, admin2.StatusCode); // owner can

        var byManager = await manager.PutWithKeyAsync(
            $"/api/v1/users/{(await FindUserAsync(admin, managerEmail)).Id}",
            Guid.NewGuid().ToString(), new { name = "Hijacked by manager" });
        Assert.Equal(HttpStatusCode.Forbidden, byManager.StatusCode);
        Assert.Equal("Set by owner", (await FindUserAsync(admin, managerEmail)).DisplayName); // unchanged
    }

    // Two concurrent PUTs of the same user must not corrupt: the Identity
    // ConcurrencyStamp makes the write a compare-and-swap, so a losing writer
    // fails closed (409, never a 500) and exactly one name persists (#163 review).
    [Fact]
    public async Task Update_ConcurrentEdits_FailClosed_NoCorruption()
    {
        var (admin, _) = await AdminAsync();
        var email = $"hand-{Guid.NewGuid():N}@test.local";
        await admin.PostWithKeyAsync("/api/v1/users", Guid.NewGuid().ToString(),
            new { email, password = TestHarness.Password, role = "Worker" });
        var user = await FindUserAsync(admin, email);

        var responses = await Task.WhenAll(
            admin.PutWithKeyAsync($"/api/v1/users/{user.Id}", Guid.NewGuid().ToString(), new { name = "Alice" }),
            admin.PutWithKeyAsync($"/api/v1/users/{user.Id}", Guid.NewGuid().ToString(), new { name = "Bob" }));

        // No unhandled concurrency exception leaked as a 500; each is 204 or 409.
        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Conflict,
            $"unexpected {(int)r.StatusCode} — a concurrency conflict must fail closed, not 500"));
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.NoContent);

        // Exactly one of the two names won — never a torn/blank value.
        var final = (await FindUserAsync(admin, email)).DisplayName;
        Assert.Contains(final, new[] { "Alice", "Bob" });
    }
}
