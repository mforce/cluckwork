using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Migrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #1284 G7.1: AppDbContext maps Identity's version 3 passkey table itself
// (record 1282 decisions 6 and 21), so every construction has it and today's
// column shapes stay. Model-only, no database.
[Collection(EnvironmentMutatingCollection.Name)]
public sealed class PasskeyStorageModelTests : IDisposable
{
    private readonly string? _originalConnection =
        Environment.GetEnvironmentVariable(AppDbContextDesignTimeFactory.ConnectionEnvVar);
    private readonly string? _originalLoopbackOptIn =
        Environment.GetEnvironmentVariable(AppDbContextDesignTimeFactory.AllowInsecureLoopbackEnvVar);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDbContextDesignTimeFactory.ConnectionEnvVar, _originalConnection);
        Environment.SetEnvironmentVariable(AppDbContextDesignTimeFactory.AllowInsecureLoopbackEnvVar, _originalLoopbackOptIn);
    }

    // `new`, with no application service provider: Identity then builds its
    // version 1 model, so only AppDbContext's own mapping can add the table.
    private static AppDbContext BareContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=none;Password=none").Options,
            new TenantContext(), new FlockScope());

    [Fact]
    public void ABareContext_MapsThePasskeyTable_WithTheTenantSeam()
    {
        using var db = BareContext();

        var passkey = db.Model.FindEntityType(typeof(IdentityUserPasskey<Guid>));
        Assert.True(passkey is not null, "a context built with `new` does not map IdentityUserPasskey<Guid>");
        Assert.Equal("AspNetUserPasskeys", passkey.GetTableName());
        Assert.Equal(["CredentialId"], passkey.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(1024, passkey.FindProperty(nameof(IdentityUserPasskey<Guid>.CredentialId))!.GetMaxLength());
        var data = passkey.FindNavigation(nameof(IdentityUserPasskey<Guid>.Data))!.TargetEntityType;
        Assert.True(data.IsOwned() && data.IsMappedToJson(), "Data must be Identity's owned JSON column");

        var accountId = passkey.FindProperty("AccountId");
        Assert.True(accountId is { IsNullable: false, IsConcurrencyToken: true }
            && accountId.IsShadowProperty() && accountId.ClrType == typeof(Guid), "AspNetUserPasskeys needs the #1276 shadow AccountId concurrency token");
        var composite = passkey.GetForeignKeys()
            .Single(fk => fk.Properties.Select(p => p.Name).SequenceEqual(["UserId", "AccountId"]));
        Assert.Equal(typeof(ApplicationUser), composite.PrincipalEntityType.ClrType);
        Assert.Equal(["Id", "AccountId"], composite.PrincipalKey.Properties.Select(p => p.Name));
        Assert.Equal(DeleteBehavior.Cascade, composite.DeleteBehavior);

        var ceremony = db.Model.FindEntityType(typeof(PasskeyCeremony));
        Assert.Equal("passkey_ceremonies", ceremony?.GetTableName());
        Assert.Null(ceremony!.FindProperty("AccountId"));

        // Version 1's shapes: version 2 would cap PhoneNumber at 256.
        Assert.Null(db.Model.FindEntityType(typeof(ApplicationUser))!
            .FindProperty(nameof(ApplicationUser.PhoneNumber))!.GetMaxLength());
    }

    // The design-time factory's model is the one `dotnet ef` diffs; no
    // pending change means the committed migration and snapshot match it,
    // passkey table included.
    [Fact]
    public void TheDesignTimeModel_HasNoPendingChange()
    {
        Environment.SetEnvironmentVariable(AppDbContextDesignTimeFactory.ConnectionEnvVar,
            "Host=localhost;Database=model-only;Username=none;Password=none");
        Environment.SetEnvironmentVariable(AppDbContextDesignTimeFactory.AllowInsecureLoopbackEnvVar, "true");

        using var db = new AppDbContextDesignTimeFactory().CreateDbContext([]);

        Assert.False(db.Database.HasPendingModelChanges(), "the model differs from AppDbContextModelSnapshot");
    }

    // One migration adds the two tables, the count and its constraint, and
    // alters no existing column.
    [Fact]
    public void TheMigration_AddsThePasskeyStorage_AndAltersNoExistingColumn()
    {
        var up = new AddPasskeyStorage().UpOperations;

        Assert.DoesNotContain(up, op => op is AlterColumnOperation or AlterTableOperation or DropColumnOperation
            or DropTableOperation or RenameColumnOperation or DropForeignKeyOperation or DropIndexOperation);
        var passkeys = Assert.Single(up.OfType<CreateTableOperation>(), t => t.Name == "AspNetUserPasskeys");
        Assert.Contains(passkeys.Columns, c => c is { Name: "AccountId", IsNullable: false } && c.ClrType == typeof(Guid));
        Assert.Contains(passkeys.ForeignKeys, fk => fk.Columns.SequenceEqual(["UserId", "AccountId"])
            && fk.PrincipalColumns!.SequenceEqual(["Id", "AccountId"]) && fk.OnDelete == ReferentialAction.Cascade);
        Assert.Single(up.OfType<CreateTableOperation>(), t => t.Name == "passkey_ceremonies");
        var count = Assert.Single(up.OfType<AddColumnOperation>());
        Assert.Equal(("AspNetUsers", "PasskeyCount", (object?)0, false), (count.Table, count.Name, count.DefaultValue, count.IsNullable));
        var check = Assert.Single(up.OfType<AddCheckConstraintOperation>());
        Assert.Equal("CK_AspNetUsers_PasskeyCount", check.Name);
    }
}

// G7.1, the DI path: the app's own registration builds the same model, so a
// schema version set there instead of the explicit mapping shows as a
// pending change.
[Collection(IntegrationCollection.Name)]
public sealed class PasskeyStorageServingModelTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public void TheServingModel_HasNoPendingChange_AndMapsThePasskeyTable()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.NotNull(db.Model.FindEntityType(typeof(IdentityUserPasskey<Guid>)));
        Assert.False(db.Database.HasPendingModelChanges(), "the serving model differs from AppDbContextModelSnapshot");
    }
}
