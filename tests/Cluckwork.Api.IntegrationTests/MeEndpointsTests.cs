namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;

[Collection(IntegrationCollection.Name)]
public sealed class MeEndpointsTests(CluckworkWebApplicationFactory factory)
{
    private sealed record MeRow(Guid Id, string Email, string? Name, string Role, string? Language);

    private async Task<HttpClient> ClientAsync(Guid accountId, string email, string? role)
    {
        await factory.SeedUserAsync(accountId, email, role);
        return factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
    }

    [Fact]
    public async Task Get_me_returns_identity_with_null_language_by_default()
    {
        var owner = $"o-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(owner);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(owner));

        var me = await client.GetFromJsonAsync<MeRow>("/api/v1/me");

        Assert.NotNull(me);
        Assert.Equal(owner, me!.Email);
        Assert.Equal(Roles.Owner, me.Role); // seeded admin → "Admin"
        Assert.Null(me.Language);
    }

    [Fact]
    public async Task Get_me_is_open_to_read_only()
    {
        var owner = $"o-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(owner);
        var ro = $"ro-{Guid.NewGuid():N}@test.local";
        var client = await ClientAsync(accountId, ro, Roles.ReadOnly);

        var res = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var me = await res.Content.ReadFromJsonAsync<MeRow>();
        Assert.Equal(Roles.ReadOnly, me!.Role);
    }
}
