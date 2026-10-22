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
    // Stored with every row or value: renaming one makes every stored key
    // unreadable or orphaned.
    internal const string LoginProvider = "Cluckwork";
    internal const string AuthenticatorKeyName = "AuthenticatorKey";
    internal const string PendingAuthenticatorName = "PendingAuthenticator";
    internal const string ProtectorPurpose = "Cluckwork.TwoFactorCredentials";
    private const string RecoveryCodePrefix = "RecoveryCode:";

    private const string AccountIdProperty = nameof(ApplicationUser.AccountId);

    internal async Task StageProtectedAsync(
        Guid accountId, Guid userId, ProtectedTwoFactorValue slot, string plaintext, CancellationToken ct)
    {
        var value = Protector(accountId, userId).Protect(plaintext);
        var name = RowName(slot);
        var row = await Rows(accountId, userId).SingleOrDefaultAsync(t => t.Name == name, ct);
        if (row is null) AddRow(userId, name, value);
        else row.Value = value;
    }

    // Null when no row exists. Throws CryptographicException when the key ring
    // cannot read the value (a certificate change, or a value copied from
    // another user's row).
    internal async Task<string?> ReadProtectedAsync(
        Guid accountId, Guid userId, ProtectedTwoFactorValue slot, CancellationToken ct)
    {
        var name = RowName(slot);
        var value = await Rows(accountId, userId).AsNoTracking()
            .Where(t => t.Name == name).Select(t => t.Value).SingleOrDefaultAsync(ct);
        return value is null ? null : Protector(accountId, userId).Unprotect(value);
    }

    // #1274 decision 9: one row per code, named by its digest, with no value.
    internal void StageRecoveryCodes(Guid userId, IEnumerable<string> codes)
    {
        foreach (var code in codes)
            AddRow(userId, RecoveryCodeName(userId, code), value: null);
    }

    // The one digest function, so the stage and verify sites cannot disagree on
    // the normal form: spaces and hyphens removed, upper case, then
    // XXXX-XXXX-XXXX-XXXX. Rejecting a malformed code is the parser's job (#1278).
    internal static string RecoveryCodeName(Guid userId, string code)
    {
        var compact = code.Replace(" ", "").Replace("-", "").ToUpperInvariant();
        if (compact.Length != 16)
            throw new ArgumentException("A recovery code has 16 characters.", nameof(code));
        var normalized = string.Join('-', compact.Chunk(4).Select(c => new string(c)));
        return RecoveryCodePrefix + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:D}:{normalized}")));
    }

    private static string RowName(ProtectedTwoFactorValue slot) => slot switch
    {
        ProtectedTwoFactorValue.AuthenticatorKey => AuthenticatorKeyName,
        ProtectedTwoFactorValue.PendingAuthenticator => PendingAuthenticatorName,
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    private IQueryable<IdentityUserToken<Guid>> Rows(Guid accountId, Guid userId) =>
        db.UserTokens.Where(t =>
            t.UserId == userId
            && EF.Property<Guid>(t, AccountIdProperty) == accountId
            && t.LoginProvider == LoginProvider);

    // The interceptor stamps AccountId from the resolved tenant, and the
    // composite foreign key refuses a user of another farm. Every add runs on a
    // signed-in route, so an add under no tenant is refused (decision 15).
    private void AddRow(Guid userId, string name, string? value) =>
        db.UserTokens.Add(new IdentityUserToken<Guid> { UserId = userId, LoginProvider = LoginProvider, Name = name, Value = value });

    private IDataProtector Protector(Guid accountId, Guid userId) =>
        dataProtection.CreateProtector(ProtectorPurpose, accountId.ToString("D"), userId.ToString("D"));
}
