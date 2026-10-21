using System.Security.Cryptography;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cluckwork.Api.IntegrationTests;

// #1276 (G1.3, G1.4) — the storage half of TwoFactorCredentials (#1274
// decisions 9 and 13). Keys and codes are generated per run, never literals.
[Collection(IntegrationCollection.Name)]
public sealed class TwoFactorCredentialsStorageTests(CluckworkWebApplicationFactory factory)
{
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
        var credentials = new TwoFactorCredentials(db, scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>());
        return await action(db, credentials);
    }

    private Task<List<IdentityUserToken<Guid>>> RowsAsync(Seeded user) =>
        factory.WithTenantScopeAsync(user.AccountId, db =>
            db.UserTokens.AsNoTracking().Where(t => t.UserId == user.UserId).ToListAsync());

    private static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(20));

    private static string NewRecoveryCode()
    {
        var raw = RandomNumberGenerator.GetString("23456789BCDFGHJKMNPQRTVWXY", 16);
        return string.Join('-', raw.Chunk(4).Select(c => new string(c)));
    }

    [Theory]
    [InlineData(nameof(ProtectedTwoFactorValue.AuthenticatorKey))]
    [InlineData(nameof(ProtectedTwoFactorValue.PendingAuthenticator))]
    public async Task AProtectedValue_IsNotThePlaintext_AndReadsBackAfterARestartAndFromASecondInstance(string slotName)
    {
        var slot = Enum.Parse<ProtectedTwoFactorValue>(slotName);
        var user = await SeedUserAsync();
        var key = NewKey();

        await using (var first = factory.WithWebHostBuilder(_ => { }))
        {
            await InScopeAsync(first.Services, user.AccountId, async (db, credentials) =>
            {
                await credentials.StageProtectedAsync(user.AccountId, user.UserId, slot, key, CancellationToken.None);
                return await db.SaveChangesAsync();
            });
        }

        var row = Assert.Single(await RowsAsync(user));
        Assert.Equal(("Cluckwork", slot.ToString()), (row.LoginProvider, row.Name));
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
                await credentials.StageProtectedAsync(user.AccountId, user.UserId, ProtectedTwoFactorValue.PendingAuthenticator, key, CancellationToken.None);
                return await db.SaveChangesAsync();
            });
        }

        Assert.Single(await RowsAsync(user));
        Assert.Equal(second, await InScopeAsync(factory.Services, user.AccountId, (_, credentials) =>
            credentials.ReadProtectedAsync(user.AccountId, user.UserId, ProtectedTwoFactorValue.PendingAuthenticator, CancellationToken.None)));
    }

    // Same farm isolates the user in the purpose; another farm isolates the account.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AValueCopiedToAnotherUsersRow_DoesNotUnprotect(bool sameFarm)
    {
        var owner = await SeedUserAsync();
        var other = await SeedUserAsync(sameFarm ? owner.AccountId : null);

        await InScopeAsync(factory.Services, owner.AccountId, async (db, credentials) =>
        {
            await credentials.StageProtectedAsync(owner.AccountId, owner.UserId, ProtectedTwoFactorValue.AuthenticatorKey, NewKey(), CancellationToken.None);
            return await db.SaveChangesAsync();
        });
        var copied = Assert.Single(await RowsAsync(owner)).Value;

        await factory.WithTenantScopeAsync(other.AccountId, async db =>
        {
            db.UserTokens.Add(new IdentityUserToken<Guid>
            {
                UserId = other.UserId,
                LoginProvider = TwoFactorCredentials.LoginProvider,
                Name = nameof(ProtectedTwoFactorValue.AuthenticatorKey),
                Value = copied,
            });
            await db.SaveChangesAsync();
        });

        var thrown = await Record.ExceptionAsync(() => InScopeAsync(factory.Services, other.AccountId, (_, credentials) =>
            credentials.ReadProtectedAsync(other.AccountId, other.UserId, ProtectedTwoFactorValue.AuthenticatorKey, CancellationToken.None)));

        Assert.IsAssignableFrom<CryptographicException>(thrown);
    }

    [Fact]
    public async Task RecoveryCodes_AreStoredOneDigestRowEach_AndNoRowHoldsAPlaintextCode()
    {
        var user = await SeedUserAsync();
        var codes = Enumerable.Range(0, 10).Select(_ => NewRecoveryCode()).ToList();

        await InScopeAsync(factory.Services, user.AccountId, (db, credentials) =>
        {
            credentials.StageRecoveryCodes(user.AccountId, user.UserId, codes);
            return db.SaveChangesAsync();
        });

        var rows = await RowsAsync(user);
        Assert.Equal(
            codes.Select(c => TwoFactorCredentials.RecoveryCodeName(user.UserId, c)).Order(),
            rows.Select(r => r.Name).Order());
        Assert.All(rows, r => Assert.Equal(("Cluckwork", (string?)null), (r.LoginProvider, r.Value)));
        Assert.All(rows, r => Assert.Matches("^RecoveryCode:[0-9a-f]{64}$", r.Name));
        foreach (var fragment in codes.SelectMany(c => new[] { c, c.Replace("-", ""), c[..4] }))
            Assert.DoesNotContain(rows, r => r.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ARecoveryCodeDigest_NamesTheUser()
    {
        var code = NewRecoveryCode();
        Assert.NotEqual(
            TwoFactorCredentials.RecoveryCodeName(Guid.NewGuid(), code),
            TwoFactorCredentials.RecoveryCodeName(Guid.NewGuid(), code));
    }

    // The login code step writes before the tenant resolves (#1274 decision 22),
    // so the row carries the caller's account and the foreign key checks it.
    [Fact]
    public async Task UnderNoResolvedTenant_RowsCarryTheCallersAccount_AndAnotherFarmIsRefused()
    {
        var user = await SeedUserAsync();
        var otherFarm = await SeedUserAsync();

        await InScopeAsync(factory.Services, tenant: null, (db, credentials) =>
        {
            credentials.StageRecoveryCodes(user.AccountId, user.UserId, [NewRecoveryCode()]);
            return db.SaveChangesAsync();
        });
        var stored = await factory.WithTenantScopeAsync(user.AccountId, db =>
            db.UserTokens.AsNoTracking().Where(t => t.UserId == user.UserId)
                .Select(t => EF.Property<Guid>(t, "AccountId")).SingleAsync());
        Assert.Equal(user.AccountId, stored);

        var thrown = await Record.ExceptionAsync(() => InScopeAsync(factory.Services, tenant: null, (db, credentials) =>
        {
            credentials.StageRecoveryCodes(otherFarm.AccountId, user.UserId, [NewRecoveryCode()]);
            return db.SaveChangesAsync();
        }));
        Assert.True(thrown is DbUpdateException { InnerException: PostgresException { SqlState: "23503" } },
            $"a row naming another farm for this user was not refused by the foreign key: {thrown?.GetType().Name ?? "none"}");
        Assert.Single(await RowsAsync(user));
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
