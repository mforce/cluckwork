namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;

[Collection(IntegrationCollection.Name)]
public sealed class IdempotencyUserScopeTests(CluckworkWebApplicationFactory factory)
{
    private sealed record MeRow(Guid Id, string Email, string? Name, string Role, string? Language);

    [Fact]
    public async Task Same_key_two_users_one_account_do_not_replay_each_other()
    {
        var ownerEmail = $"o-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);

        var aEmail = $"a-{Guid.NewGuid():N}@test.local";
        var bEmail = $"b-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, aEmail, Roles.Manager);
        await factory.SeedUserAsync(accountId, bEmail, Roles.Sales);
        var a = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(aEmail));
        var b = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(bEmail));

        var sharedKey = Guid.NewGuid().ToString();
        var ra = await a.PutWithKeyAsync("/api/v1/me/language", sharedKey, new { language = "en" });
        var rb = await b.PutWithKeyAsync("/api/v1/me/language", sharedKey, new { language = "fr" });
        Assert.Equal(HttpStatusCode.NoContent, ra.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, rb.StatusCode);

        // B's write was NOT skipped by replaying A's cached response.
        Assert.Equal("en", (await a.GetFromJsonAsync<MeRow>("/api/v1/me"))!.Language);
        Assert.Equal("fr", (await b.GetFromJsonAsync<MeRow>("/api/v1/me"))!.Language);
    }

    [Fact]
    public async Task Same_user_same_key_sameBody_stillReplays()
    {
        var email = $"o-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var key = Guid.NewGuid().ToString();
        var first = await client.PutWithKeyAsync("/api/v1/me/language", key, new { language = "en" });
        // Same key, SAME body: a genuine replay returns the cached 204 without
        // re-executing the handler.
        var second = await client.PutWithKeyAsync("/api/v1/me/language", key, new { language = "en" });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal("en", (await client.GetFromJsonAsync<MeRow>("/api/v1/me"))!.Language);
    }

    // #307 — reusing a key with a DIFFERENT request payload is a protocol
    // conflict, not a replay: the handler must never run a second time under
    // a caller-supplied guess at what the first request "really" was.
    [Fact]
    public async Task Same_user_same_key_differentBody_returnsConflict_andNeverReexecutes()
    {
        var email = $"o-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var key = Guid.NewGuid().ToString();
        var first = await client.PutWithKeyAsync("/api/v1/me/language", key, new { language = "en" });
        // Different body, same key: a conflict, never a re-execution — the
        // language must stay "en" (if the handler ran again with "fr" this
        // would flip and the last assertion below would fail).
        var second = await client.PutWithKeyAsync("/api/v1/me/language", key, new { language = "fr" });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("en", (await client.GetFromJsonAsync<MeRow>("/api/v1/me"))!.Language);
    }
}
