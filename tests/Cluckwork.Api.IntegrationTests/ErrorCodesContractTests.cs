namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;

[Collection(IntegrationCollection.Name)]
public sealed class ErrorCodesContractTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task Uncoded_validator_400_keeps_errors_and_emits_no_errorCodes()
    {
        // CreateUser with a blank email trips a FluentValidation rule that has NO
        // explicit code — so `errors` is present and `errorCodes` is absent.
        var owner = $"o-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(owner);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(owner));

        var res = await client.PostWithKeyAsync("/api/v1/users", Guid.NewGuid().ToString(),
            new { email = "", password = "x", role = "ReadOnly" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("errors", out _));
        Assert.False(doc.RootElement.TryGetProperty("errorCodes", out _));
    }
}
