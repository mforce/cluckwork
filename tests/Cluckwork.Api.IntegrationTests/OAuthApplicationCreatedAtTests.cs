using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace Cluckwork.Api.IntegrationTests;

// #797 — an application's CreatedAtUtc comes from #819's StampCreatedBusinessRecord
// trigger: the database stamps it on insert, whatever the code sends, and keeps it on
// every update.
[Collection(IntegrationCollection.Name)]
public sealed class OAuthApplicationCreatedAtTests(CluckworkWebApplicationFactory factory)
{
    private static readonly DateTimeOffset Supplied = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SuppliedCreatedAt_IsReplacedByTheDatabaseStamp()
    {
        var clientId = $"client-{Guid.NewGuid():N}";
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "OpenIddictApplications" ("Id", "ClientId", "CreatedAtUtc")
            VALUES ({Guid.NewGuid()}, {clientId}, {Supplied})
            """);

        Assert.True(await IsFreshAsync(db, clientId), "the database kept a supplied creation time");
    }

    [Fact]
    public async Task EfInsert_ReadsBackTheDatabaseStamp()
    {
        var application = new OpenIddictEntityFrameworkCoreApplication<Guid> { ClientId = $"client-{Guid.NewGuid():N}" };
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.OAuthApplications.Add(application);
        var createdAt = db.Entry(application).Property<DateTimeOffset>("CreatedAtUtc");
        createdAt.CurrentValue = Supplied;

        await db.SaveChangesAsync();

        Assert.True(await IsFreshAsync(db, application.ClientId!), "the database kept a supplied creation time");
        Assert.True(createdAt.CurrentValue == await StoredAsync(db, application.ClientId!),
            "EF kept a creation time the database replaced");
    }

    [Fact]
    public async Task Updates_KeepCreatedAtUtc()
    {
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = await StoredAsync(db, clientId);

        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = (await applications.FindByClientIdAsync(clientId))!;
        var descriptor = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(descriptor, application);
        descriptor.DisplayName = "Renamed by OpenIddict";
        await applications.UpdateAsync(application, descriptor);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "OpenIddictApplications" SET "CreatedAtUtc" = {Supplied} WHERE "ClientId" = {clientId}""");

        Assert.True(await StoredAsync(db, clientId) == before, "an update changed CreatedAtUtc");
        Assert.Equal("Renamed by OpenIddict", await applications.GetDisplayNameAsync(application));
    }

    private static async Task<bool> IsFreshAsync(AppDbContext db, string clientId) =>
        await db.Database.SqlQuery<bool>($"""
            SELECT "CreatedAtUtc" > now() - interval '1 minute' AS "Value"
            FROM "OpenIddictApplications" WHERE "ClientId" = {clientId}
            """).SingleAsync();

    private static async Task<DateTimeOffset> StoredAsync(AppDbContext db, string clientId) =>
        await db.Database.SqlQuery<DateTimeOffset>($"""
            SELECT "CreatedAtUtc" AS "Value" FROM "OpenIddictApplications" WHERE "ClientId" = {clientId}
            """).SingleAsync();
}
