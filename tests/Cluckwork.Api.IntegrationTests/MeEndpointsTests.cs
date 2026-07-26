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

    private static readonly string[] EveryRole =
        [Roles.Owner, Roles.Manager, Roles.Sales, Roles.ReadOnly, "Worker"];

    [Theory]
    [MemberData(nameof(RoleCases))]
    public async Task Any_role_can_set_and_read_back_their_language(string roleCase)
    {
        var role = roleCase == "Worker" ? null : roleCase;
        var owner = $"o-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(owner);
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var client = await ClientAsync(accountId, email, role);

        var put = await client.PutWithKeyAsync(
            "/api/v1/me/language", Guid.NewGuid().ToString(), new { language = "EN" });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var me = await client.GetFromJsonAsync<MeRow>("/api/v1/me");
        Assert.Equal("en", me!.Language); // trimmed + lowercased
    }

    public static IEnumerable<object[]> RoleCases() =>
        EveryRole.Select(r => new object[] { r });

    [Fact]
    public async Task Language_is_trimmed_and_lowercased_end_to_end()
    {
        var email = $"o-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var put = await client.PutWithKeyAsync(
            "/api/v1/me/language", Guid.NewGuid().ToString(), new { language = " EN " });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var me = await client.GetFromJsonAsync<MeRow>("/api/v1/me");
        Assert.Equal("en", me!.Language);
    }

    [Fact]
    public async Task Null_clears_the_language()
    {
        var email = $"o-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        await client.PutWithKeyAsync("/api/v1/me/language", Guid.NewGuid().ToString(), new { language = "fr" });
        var setRes = await client.PutWithKeyAsync(
            "/api/v1/me/language", Guid.NewGuid().ToString(), new { language = (string?)null });
        Assert.Equal(HttpStatusCode.NoContent, setRes.StatusCode);

        var me = await client.GetFromJsonAsync<MeRow>("/api/v1/me");
        Assert.Null(me!.Language);
    }

    [Theory]
    [InlineData("en-US")]   // regional variant, not a primary subtag
    [InlineData("")]        // empty is not another null
    [InlineData("   ")]     // whitespace collapses to empty
    [InlineData("abcdefghi")] // 9 letters > max 8
    [InlineData("en1")]     // non-letter
    public async Task Malformed_language_is_a_coded_400(string bad)
    {
        var email = $"o-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var res = await client.PutWithKeyAsync(
            "/api/v1/me/language", Guid.NewGuid().ToString(), new { language = bad });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var codes = doc.RootElement.GetProperty("errorCodes").GetProperty("Language");
        Assert.Equal("Me.Language.Format", codes[0].GetString());
        // Index-aligned with errors.
        var errors = doc.RootElement.GetProperty("errors").GetProperty("Language");
        Assert.Equal(errors.GetArrayLength(), codes.GetArrayLength());
    }

    [Fact]
    public async Task Absent_field_is_a_400()
    {
        var email = $"o-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        // {} — the required field is missing (a malformed body, not a clear).
        var res = await client.PutWithKeyAsync(
            "/api/v1/me/language", Guid.NewGuid().ToString(), new { });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
