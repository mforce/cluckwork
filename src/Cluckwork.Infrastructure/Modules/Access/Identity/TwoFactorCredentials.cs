using System.Security.Cryptography;
using System.Text;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

internal enum ProtectedTwoFactorValue
{
    AuthenticatorKey,
    PendingAuthenticator,
}

// #1274 "The state it owns": the one owner of two-factor state. #1276 builds
// the storage; #1278 onward add the methods that call it. Every row is
// LoginProvider "Cluckwork", so Identity's own "[AspNetUserStore]" rows are
// never written. The stage methods write nothing; the caller's save does.
internal sealed class TwoFactorCredentials(AppDbContext db, IDataProtectionProvider dataProtection)
{
    internal const string LoginProvider = "Cluckwork";
    private const string RecoveryCodePrefix = "RecoveryCode:";
    private const string AccountId = nameof(ApplicationUser.AccountId);

    // Part of every stored value: changing it makes every key unreadable.
    private const string ProtectorPurpose = "Cluckwork.TwoFactorCredentials";

    internal async Task StageProtectedAsync(
        Guid accountId, Guid userId, ProtectedTwoFactorValue slot, string plaintext, CancellationToken ct)
    {
        var value = Protector(accountId, userId).Protect(plaintext);
        var name = slot.ToString();
        var row = await Rows(accountId, userId).SingleOrDefaultAsync(t => t.Name == name, ct);
        if (row is null) AddRow(accountId, userId, name, value);
        else row.Value = value;
    }

    // Null when no row exists. Throws CryptographicException when the key ring
    // cannot read the value (a certificate change, or a value copied from
    // another user's row).
    internal async Task<string?> ReadProtectedAsync(
        Guid accountId, Guid userId, ProtectedTwoFactorValue slot, CancellationToken ct)
    {
        var name = slot.ToString();
        var value = await Rows(accountId, userId).AsNoTracking()
            .Where(t => t.Name == name).Select(t => t.Value).SingleOrDefaultAsync(ct);
        return value is null ? null : Protector(accountId, userId).Unprotect(value);
    }

    // #1274 decision 9: one row per code, named by its digest, with no value.
    internal void StageRecoveryCodes(Guid accountId, Guid userId, IEnumerable<string> normalizedCodes)
    {
        foreach (var code in normalizedCodes)
            AddRow(accountId, userId, RecoveryCodeName(userId, code), value: null);
    }

    internal static string RecoveryCodeName(Guid userId, string normalizedCode) =>
        RecoveryCodePrefix + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:D}:{normalizedCode}")));

    private IQueryable<IdentityUserToken<Guid>> Rows(Guid accountId, Guid userId) =>
        db.UserTokens.Where(t =>
            t.UserId == userId
            && EF.Property<Guid>(t, AccountId) == accountId
            && t.LoginProvider == LoginProvider);

    // The account is set, not left to the interceptor, because the login code
    // step writes before the tenant resolves. The composite foreign key then
    // proves the user belongs to that account.
    private void AddRow(Guid accountId, Guid userId, string name, string? value)
    {
        var row = new IdentityUserToken<Guid> { UserId = userId, LoginProvider = LoginProvider, Name = name, Value = value };
        db.UserTokens.Add(row);
        db.Entry(row).Property(AccountId).CurrentValue = accountId;
    }

    private IDataProtector Protector(Guid accountId, Guid userId) =>
        dataProtection.CreateProtector(ProtectorPurpose, accountId.ToString("D"), userId.ToString("D"));
}
