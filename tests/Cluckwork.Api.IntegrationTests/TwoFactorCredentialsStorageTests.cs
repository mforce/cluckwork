using System.Security.Cryptography;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #1276 (G1.3, G1.4) — the storage half of TwoFactorCredentials (#1274
// decisions 9 and 13). Keys and codes are generated per run; the persisted
// names and the purpose are pinned as literals, because a rename orphans every
// stored value.
[Collection(IntegrationCollection.Name)]
public sealed class TwoFactorCredentialsStorageTests(CluckworkWebApplicationFactory factory)
{
    private const string Purpose = "Cluckwork.TwoFactorCredentials";

    private sealed record Seeded(Guid AccountId, Guid UserId, string Email);

    private async Task<Seeded> SeedUserAsync(Guid? accountId = null)
    {
        var email = $"tf-{Guid.NewGuid():N}@test.local";
        var account = accountId ?? await factory.SeedAccountWithUserAsync(email);
        if (accountId is not null) await factory.SeedUserAsync(account, email, asAdmin: false);
        var userId = await factory.WithTenantScopeAsync(account, db =>
            db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        return new Seeded(account, userId, email);
    }

    private static async Task<T> InScopeAsync<T>(
        IServiceProvider services, Guid? tenant, Func<AppDbContext, TwoFactorCredentials, Task<T>> action)
    {
        using var scope = services.CreateScope();
        if (tenant is { } accountId) scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var credentials = (TwoFactorCredentials)scope.ServiceProvider.GetRequiredService<ITwoFactorCredentials>();
        return await action(db, credentials);
    }

    private Task<List<IdentityUserToken<Guid>>> RowsAsync(Seeded user) =>
        factory.WithTenantScopeAsync(user.AccountId, db =>
            db.UserTokens.AsNoTracking().Where(t => t.UserId == user.UserId).ToListAsync());

    private Task InsertRowAsync(Seeded user, string name, string value) =>
        factory.WithTenantScopeAsync(user.AccountId, async db =>
        {
            db.UserTokens.Add(new IdentityUserToken<Guid> { UserId = user.UserId, LoginProvider = "Cluckwork", Name = name, Value = value });
            await db.SaveChangesAsync();
        });

    private Task<string?> ReadKeyAsync(Seeded user) =>
        InScopeAsync(factory.Services, user.AccountId, (_, credentials) =>
            credentials.ReadProtectedAsync(user.AccountId, user.UserId, ProtectedTwoFactorValue.AuthenticatorKey, CancellationToken.None));

    private static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(20));

    private static string NewRecoveryCode()
    {
        var raw = RandomNumberGenerator.GetString("23456789BCDFGHJKMNPQRTVWXY", 16);
        return string.Join('-', raw.Chunk(4).Select(c => new string(c)));
    }

    // The row names stay literals, never ProtectedTwoFactorValue.RowName: a
    // rename of the stored name must turn this red.
    [Theory]
    [InlineData(true, "AuthenticatorKey")]
    [InlineData(false, "PendingAuthenticator")]
    public async Task AProtectedValue_IsNotThePlaintext_AndReadsBackAfterARestartAndFromASecondInstance(bool liveKey, string rowName)
    {
        var slot = liveKey ? ProtectedTwoFactorValue.AuthenticatorKey : ProtectedTwoFactorValue.PendingAuthenticator;
        var user = await SeedUserAsync();
        var key = NewKey();

        await using (var first = factory.WithWebHostBuilder(_ => { }))
        {
            await InScopeAsync(first.Services, user.AccountId, async (db, credentials) =>
            {
                await credentials.StageProtectedAsync(user.UserId, slot, key, CancellationToken.None);
                return await db.SaveChangesAsync();
            });
        }

        var row = Assert.Single(await RowsAsync(user));
        Assert.Equal(("Cluckwork", rowName), (row.LoginProvider, row.Name));
        Assert.False(string.IsNullOrEmpty(row.Value), "the protected row has no value");
        Assert.DoesNotContain(key, row.Value, StringComparison.OrdinalIgnoreCase);

        // A fresh host loads the ring from the database, as a restarted process does;
        // the collection's host, alive throughout, is a second instance.
        await using var restarted = factory.WithWebHostBuilder(_ => { });
        foreach (var host in new[] { restarted.Services, factory.Services })
        {
            var read = await InScopeAsync(host, user.AccountId, (_, credentials) =>
                credentials.ReadProtectedAsync(user.AccountId, user.UserId, slot, CancellationToken.None));
            Assert.Equal(key, read);
        }
    }

    [Fact]
    public async Task StagingAgain_ReplacesTheValue_InTheSameRow()
    {
        var user = await SeedUserAsync();
        var second = NewKey();

        foreach (var key in new[] { NewKey(), second })
        {
            await InScopeAsync(factory.Services, user.AccountId, async (db, credentials) =>
            {
                await credentials.StageProtectedAsync(user.UserId, ProtectedTwoFactorValue.PendingAuthenticator, key, CancellationToken.None);
                return await db.SaveChangesAsync();
            });
        }

        Assert.Single(await RowsAsync(user));
        Assert.Equal(second, await InScopeAsync(factory.Services, user.AccountId, (_, credentials) =>
            credentials.ReadProtectedAsync(user.AccountId, user.UserId, ProtectedTwoFactorValue.PendingAuthenticator, CancellationToken.None)));
    }

    // The purpose chain is pinned directly: user ids are globally unique, so no
    // copied row can tell whether the account is in the purpose.
    [Fact]
    public async Task TheStoredValue_IsProtectedWithThePurpose_ThenTheAccount_ThenTheUser()
    {
        var user = await SeedUserAsync();
        var key = NewKey();
        var provider = factory.Services.GetRequiredService<IDataProtectionProvider>();
        await InsertRowAsync(user, "AuthenticatorKey",
            provider.CreateProtector(Purpose, user.AccountId.ToString("D"), user.UserId.ToString("D")).Protect(key));

        Assert.Equal(key, await ReadKeyAsync(user));
    }

    [Theory]
    [InlineData("another account")]
    [InlineData("swapped order")]
    [InlineData("no account")]
    public async Task AValueProtectedWithAnyOtherPurposeChain_DoesNotUnprotect(string variant)
    {
        var user = await SeedUserAsync();
        var account = user.AccountId.ToString("D");
        var userId = user.UserId.ToString("D");
        string[] chain = variant switch
        {
            "another account" => [Guid.NewGuid().ToString("D"), userId],
            "swapped order" => [userId, account],
            _ => [userId],
        };
        var provider = factory.Services.GetRequiredService<IDataProtectionProvider>();
        await InsertRowAsync(user, "AuthenticatorKey", provider.CreateProtector(Purpose, chain).Protect(NewKey()));

        var thrown = await Record.ExceptionAsync(() => ReadKeyAsync(user));

        Assert.IsAssignableFrom<CryptographicException>(thrown);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AValueCopiedToAnotherUsersRow_DoesNotUnprotect(bool sameFarm)
    {
        var owner = await SeedUserAsync();
        var other = await SeedUserAsync(sameFarm ? owner.AccountId : null);

        await InScopeAsync(factory.Services, owner.AccountId, async (db, credentials) =>
        {
            await credentials.StageProtectedAsync(owner.UserId, ProtectedTwoFactorValue.AuthenticatorKey, NewKey(), CancellationToken.None);
            return await db.SaveChangesAsync();
        });
        await InsertRowAsync(other, "AuthenticatorKey", Assert.Single(await RowsAsync(owner)).Value!);

        var thrown = await Record.ExceptionAsync(() => ReadKeyAsync(other));

        Assert.IsAssignableFrom<CryptographicException>(thrown);
    }

    [Fact]
    public async Task RecoveryCodes_AreStoredOneDigestRowEach_AndNoRowHoldsAPlaintextCode()
    {
        var user = await SeedUserAsync();
        var codes = Enumerable.Range(0, 10).Select(_ => NewRecoveryCode()).ToList();

        await InScopeAsync(factory.Services, user.AccountId, (db, credentials) =>
        {
            credentials.StageRecoveryCodes(user.UserId, codes);
            return db.SaveChangesAsync();
        });

        var rows = await RowsAsync(user);
        Assert.Equal(
            codes.Select(c => TwoFactorCredentials.RecoveryCodeName(user.UserId, c)).Order(),
            rows.Select(r => r.Name).Order());
        Assert.All(rows, r => Assert.Equal(("Cluckwork", (string?)null), (r.LoginProvider, r.Value)));
        Assert.All(rows, r => Assert.Matches("^RecoveryCode:[0-9a-f]{64}$", r.Name));
        foreach (var code in codes.SelectMany(c => new[] { c, c.Replace("-", "") }))
            Assert.DoesNotContain(rows, r => r.Name.Contains(code, StringComparison.OrdinalIgnoreCase));
    }

    // Pins the construction before rows exist anywhere: lower-hex SHA-256 of the
    // UTF-8 string "<userId:D>:<XXXX-XXXX-XXXX-XXXX>" (#1274 decision 9).
    [Theory]
    [InlineData("2222-3333-4444-5555")]
    [InlineData("2222333344445555")]
    [InlineData(" 2222-3333 4444-5555 ")]
    public void ARecoveryCodeDigest_IsTheFixedConstruction_OverTheNormalForm(string typed)
    {
        Assert.Equal(
            "RecoveryCode:5905685ffb2fcec0f9fd094c3ec76b3fcbef395a725a8e62c712a93525e32731",
            TwoFactorCredentials.RecoveryCodeName(Guid.Parse("0000000a-0000-0000-0000-000000001276"), typed));
    }

    [Theory]
    [InlineData("2222-3333-4444-555")]
    [InlineData("2222-3333-4444-55556")]
    [InlineData("")]
    public void ARecoveryCodeDigest_RefusesACodeThatIsNot16CharactersAfterNormalizing(string typed)
    {
        Assert.Throws<ArgumentException>(() => TwoFactorCredentials.RecoveryCodeName(Guid.NewGuid(), typed));
    }

    [Fact]
    public void ARecoveryCodeDigest_IgnoresCase()
    {
        var code = NewRecoveryCode();
        var userId = Guid.NewGuid();
        Assert.Equal(
            TwoFactorCredentials.RecoveryCodeName(userId, code),
            TwoFactorCredentials.RecoveryCodeName(userId, code.ToLowerInvariant()));
    }

    [Fact]
    public void ARecoveryCodeDigest_NamesTheUser()
    {
        var code = NewRecoveryCode();
        Assert.NotEqual(
            TwoFactorCredentials.RecoveryCodeName(Guid.NewGuid(), code),
            TwoFactorCredentials.RecoveryCodeName(Guid.NewGuid(), code));
    }

    [Fact]
    public async Task AMalformedCode_StagesNoRowAtAll()
    {
        var user = await SeedUserAsync();

        var tracked = await InScopeAsync(factory.Services, user.AccountId, (db, credentials) =>
        {
            var thrown = Record.Exception(() =>
                credentials.StageRecoveryCodes(user.UserId, [NewRecoveryCode(), "2222-3333"]));
            Assert.IsType<ArgumentException>(thrown);
            return Task.FromResult(db.ChangeTracker.Entries<IdentityUserToken<Guid>>().Count());
        });

        Assert.Equal(0, tracked);
    }

    // #1274 decision 15: every add runs on a signed-in route, so a stage call
    // under no resolved tenant throws before it writes. The raw add's foreign-key
    // refusal is UserTokenTenantWriteTests.Add_UnderNoResolvedTenant_IsRefusedByTheForeignKey.
    [Fact]
    public async Task UnderNoResolvedTenant_EveryStage_Throws_AndNothingIsStored()
    {
        var user = await SeedUserAsync();

        var codes = await Record.ExceptionAsync(() => InScopeAsync(factory.Services, tenant: null, (db, credentials) =>
        {
            credentials.StageRecoveryCodes(user.UserId, [NewRecoveryCode()]);
            return db.SaveChangesAsync();
        }));
        var key = await Record.ExceptionAsync(() => InScopeAsync(factory.Services, tenant: null, async (db, credentials) =>
        {
            await credentials.StageProtectedAsync(user.UserId, ProtectedTwoFactorValue.AuthenticatorKey, NewKey(), CancellationToken.None);
            return await db.SaveChangesAsync();
        }));

        Assert.IsType<InvalidOperationException>(codes);
        Assert.IsType<InvalidOperationException>(key);
        Assert.Empty(await RowsAsync(user));
    }

    // The stage call stamps the row with the resolved tenant and reads back
    // with that account. A read naming any other account id finds no row.
    [Fact]
    public async Task AValueStagedUnderATenant_ReadsBackWithThatAccountOnly()
    {
        var user = await SeedUserAsync();
        var key = NewKey();

        await InScopeAsync(factory.Services, user.AccountId, async (db, credentials) =>
        {
            await credentials.StageProtectedAsync(user.UserId, ProtectedTwoFactorValue.AuthenticatorKey, key, CancellationToken.None);
            return await db.SaveChangesAsync();
        });
        var stamped = await factory.WithTenantScopeAsync(user.AccountId, db =>
            db.UserTokens.AsNoTracking().Where(t => t.UserId == user.UserId)
                .Select(t => EF.Property<Guid>(t, "AccountId")).SingleAsync());
        var other = await InScopeAsync(factory.Services, user.AccountId, (_, credentials) =>
            credentials.ReadProtectedAsync(Guid.NewGuid(), user.UserId, ProtectedTwoFactorValue.AuthenticatorKey, CancellationToken.None));

        Assert.Equal(user.AccountId, stamped);
        Assert.Equal(key, await ReadKeyAsync(user));
        Assert.Null(other);
    }

    // G1.4 — ProtectPersonalData stays off (#1274 decision 13): it would encrypt
    // these columns, and the NormalizedEmail lookup would stop matching.
    [Fact]
    public async Task UserNameAndEmail_StayPlaintextInTheTable()
    {
        var user = await SeedUserAsync();

        var stored = await factory.WithTenantScopeAsync(user.AccountId, db =>
            db.Database.SqlQuery<string>(
                $"""SELECT "UserName" || '|' || "Email" || '|' || "NormalizedEmail" AS "Value" FROM "AspNetUsers" WHERE "Id" = {user.UserId}""")
                .SingleAsync());

        Assert.Equal($"{user.Email}|{user.Email}|{user.Email.ToUpperInvariant()}", stored);
    }
}
