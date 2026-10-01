namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

[Collection(IntegrationCollection.Name)]
public sealed class UserLanguageColumnTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task Language_defaults_null_and_round_trips_value_then_clears()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);

        var initial = await factory.WithTenantScopeAsync(accountId, db =>
            db.Users.Where(u => u.Email == email).Select(u => u.Language).SingleAsync());
        Assert.Null(initial);

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Email == email);
            user.Language = "en";
            await db.SaveChangesAsync();
        });
        var afterSet = await factory.WithTenantScopeAsync(accountId, db =>
            db.Users.Where(u => u.Email == email).Select(u => u.Language).SingleAsync());
        Assert.Equal("en", afterSet);

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Email == email);
            user.Language = null;
            await db.SaveChangesAsync();
        });
        var afterClear = await factory.WithTenantScopeAsync(accountId, db =>
            db.Users.Where(u => u.Email == email).Select(u => u.Language).SingleAsync());
        Assert.Null(afterClear);
    }
}
