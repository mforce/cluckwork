namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;

[Collection(IntegrationCollection.Name)]
public sealed class ErrorCodesContractTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task Coded_validator_400_emits_errorCodes_alongside_errors()
    {
        // Since #231 every validator carries explicit `Feature.Field.Rule` codes,
        // so a real 400 now emits the additive `errorCodes` map next to `errors`.
        // CreateUser with a blank email trips User.Email.Required, whose explicit
        // code must appear in errorCodes. (The complementary "no explicit code →
        // errorCodes omitted" branch is no longer reachable through a live
        // endpoint; it is covered as a pure-function case in
        // ValidationResponseTests.Default_framework_code_never_leaks.)
        var owner = $"o-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(owner);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(owner));

        var res = await client.PostWithKeyAsync("/api/v1/users", Guid.NewGuid().ToString(),
            new { email = "", password = "x", role = "ReadOnly" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("errors", out _));
        Assert.True(doc.RootElement.TryGetProperty("errorCodes", out var codes));
        // The blank-email failure carries its explicit, dotted code.
        Assert.Contains("User.Email.Required", codes.GetRawText());
    }
}
