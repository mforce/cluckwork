using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Api.IntegrationTests;

public sealed partial class CredentialEpochFloorTests
{
    // #1285 G8.6: a stored epoch below 1 is refused at passkey sign-in with the
    // one Auth.PasskeyRefused body, logged once, and mints nothing.
    [Fact]
    public async Task PasskeySignIn_RefusesAnEpochBelowOne_AndLogsOnce()
    {
        var email = $"epoch-floor-pk-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var userId = await factory.WithTenantScopeAsync(accountId, db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        var key = new SoftwareAuthenticator();
        await factory.SeedPasskeyAsync(accountId, userId, key);
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "AspNetUsers" DROP CONSTRAINT IF EXISTS "CK_AspNetUsers_CredentialEpoch" """);
            return await db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "AspNetUsers" SET "CredentialEpoch" = 0 WHERE "Id" = {userId}""");
        });
        var client = factory.CreateClient(TestHarness.Cookieless(factory));
        var options = await client.PostAsync("/api/v1/auth/login/passkey/options", null);
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        var ceremony = await options.Content.ReadFromJsonAsync<JsonElement>();
        var before = CountEvents(SecurityEvents.PasskeyFailed);

        var response = await client.PostAsJsonAsync("/api/v1/auth/login/passkey", new
        {
            ceremony = ceremony.GetProperty("ceremony").GetGuid(),
            credential = JsonDocument.Parse(key.Assert(ceremony.GetProperty("options"))).RootElement,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(PasskeyErrorCodes.Refused, await TitleAsync(response));
        Assert.Equal(before + 1, CountEvents(SecurityEvents.PasskeyFailed));
        Assert.Empty(await TokenRowsAsync(accountId, userId));
    }
}
