using System.Security.Cryptography;
using System.Text;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// The two protected rows, a closed set. The row name is stored with every
// value: renaming one orphans every stored key.
internal sealed class ProtectedTwoFactorValue
{
    internal static readonly ProtectedTwoFactorValue AuthenticatorKey = new("AuthenticatorKey");
    internal static readonly ProtectedTwoFactorValue PendingAuthenticator = new("PendingAuthenticator");

    private ProtectedTwoFactorValue(string rowName) => RowName = rowName;

    internal string RowName { get; }
}

// #1274 "The state it owns": the one owner of two-factor state. #1276 builds
// the storage; #1278 onward add the methods that call it. Every row is
// LoginProvider "Cluckwork", so Identity's own "[AspNetUserStore]" rows are
// never written. The stage methods write nothing; the caller's save does.
internal sealed class TwoFactorCredentials(AppDbContext db, IDataProtectionProvider dataProtection, TenantContext tenant)
{
    // Stored with every row or value: changing one makes every stored key
    // unreadable or orphaned.
    private const string LoginProvider = "Cluckwork";
    private const string ProtectorPurpose = "Cluckwork.TwoFactorCredentials";
    private const string RecoveryCodePrefix = "RecoveryCode:";

    private const string AccountIdProperty = nameof(ApplicationUser.AccountId);

    // Every add runs on a signed-in route (#1274 decision 15), so the stage
    // methods take the account from the resolved tenant: a value protected
    // under one account and stamped with another could never be read.
    internal async Task StageProtectedAsync(
        Guid userId, ProtectedTwoFactorValue slot, string plaintext, CancellationToken ct)
    {
        var accountId = ResolvedAccountId();
        var value = Protector(accountId, userId).Protect(plaintext);
        var row = await Rows(accountId, userId).SingleOrDefaultAsync(t => t.Name == slot.RowName, ct);
        if (row is null) AddRow(userId, slot.RowName, value);
        else row.Value = value;
    }

    // Takes the account, because login reads before the tenant resolves.
    // Null when no row exists. Throws CryptographicException when the key ring
    // cannot read the value (a certificate change, or a value copied from
    // another user's row).
    internal async Task<string?> ReadProtectedAsync(
        Guid accountId, Guid userId, ProtectedTwoFactorValue slot, CancellationToken ct)
    {
        var value = await Rows(accountId, userId).AsNoTracking()
            .Where(t => t.Name == slot.RowName).Select(t => t.Value).SingleOrDefaultAsync(ct);
        return value is null ? null : Protector(accountId, userId).Unprotect(value);
    }

    // #1274 decision 9: one row per code, named by its digest, with no value.
    // Refuses before tracking anything: no tenant, or any malformed code, stages
    // no row at all.
    internal void StageRecoveryCodes(Guid userId, IEnumerable<string> codes)
    {
        _ = ResolvedAccountId();
        var names = codes.Select(code => RecoveryCodeName(userId, code)).ToList();
        foreach (var name in names)
            AddRow(userId, name, value: null);
    }

    // The one digest function, so the stage and verify sites cannot disagree on
    // the normal form: spaces and hyphens removed, upper case, then
    // XXXX-XXXX-XXXX-XXXX. The #1278 parser rejects malformed input before it
    // gets here; the length check fails closed if a caller skips it.
    internal static string RecoveryCodeName(Guid userId, string code)
    {
        var compact = code.Replace(" ", "").Replace("-", "").ToUpperInvariant();
        if (compact.Length != 16)
            throw new ArgumentException("A recovery code has 16 characters.", nameof(code));
        var normalized = string.Join('-', compact.Chunk(4).Select(c => new string(c)));
        return RecoveryCodePrefix + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:D}:{normalized}")));
    }

    private Guid ResolvedAccountId() => tenant.IsResolved
        ? tenant.AccountId
        : throw new InvalidOperationException("Two-factor rows are staged only under a resolved tenant.");

    private IQueryable<IdentityUserToken<Guid>> Rows(Guid accountId, Guid userId) =>
        db.UserTokens.Where(t =>
            t.UserId == userId
            && EF.Property<Guid>(t, AccountIdProperty) == accountId
            && t.LoginProvider == LoginProvider);

    // The interceptor stamps AccountId from the resolved tenant, and the
    // composite foreign key refuses a user of another farm.
    private void AddRow(Guid userId, string name, string? value) =>
        db.UserTokens.Add(new IdentityUserToken<Guid> { UserId = userId, LoginProvider = LoginProvider, Name = name, Value = value });

    private IDataProtector Protector(Guid accountId, Guid userId) =>
        dataProtection.CreateProtector(ProtectorPurpose, accountId.ToString("D"), userId.ToString("D"));
}
