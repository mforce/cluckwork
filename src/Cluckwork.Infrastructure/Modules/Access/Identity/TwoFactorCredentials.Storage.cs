using System.Security.Cryptography;
using System.Text;
using Cluckwork.Application.Modules.Access.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1274 "The state it owns": the protected rows, the recovery-code digests
// and the locked user's save.
internal sealed partial class TwoFactorCredentials
{
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
    // cannot read the value (a certificate change that did not keep the old
    // certificate listed, or a value copied from another user's row).
    internal async Task<string?> ReadProtectedAsync(
        Guid accountId, Guid userId, ProtectedTwoFactorValue slot, CancellationToken ct)
    {
        var value = await Rows(accountId, userId).AsNoTracking()
            .Where(t => t.Name == slot.RowName).Select(t => t.Value).SingleOrDefaultAsync(ct);
        return value is null ? null : Protector(accountId, userId).Unprotect(value);
    }

    // #1274 decision 9: one row per code, named by its digest, with no value.
    // No tenant, a malformed code or a duplicate throws before any row is tracked.
    internal void StageRecoveryCodes(Guid userId, IEnumerable<string> codes)
    {
        _ = ResolvedAccountId();
        var names = codes.Select(code => RecoveryCodeName(userId, code)).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            throw new ArgumentException("Recovery codes must be distinct after normalization.", nameof(codes));
        foreach (var name in names)
            AddRow(userId, name, value: null);
    }

    // The one digest function, so the stage and verify sites cannot disagree on
    // the normal form: spaces and hyphens removed, upper case, then
    // XXXX-XXXX-XXXX-XXXX. The verify site parses first (SecondFactorCode), so
    // malformed input never reaches here; the normal-form check fails closed if
    // a caller skips it.
    internal static string RecoveryCodeName(Guid userId, string code)
    {
        var normalized = SecondFactorCode.NormalizeRecovery(code)
            ?? throw new ArgumentException("A recovery code has 16 characters of the recovery alphabet.", nameof(code));
        return RecoveryCodePrefix + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:D}:{normalized}")));
    }

    // Distinct by construction: every code is already in its normal form.
    private static List<string> NewRecoveryCodes()
    {
        var codes = new List<string>(RecoveryCodeCount);
        while (codes.Count < RecoveryCodeCount)
        {
            var code = SecondFactorCode.NormalizeRecovery(
                RandomNumberGenerator.GetString(SecondFactorCode.RecoveryAlphabet, SecondFactorCode.RecoveryLength))!;
            if (!codes.Contains(code)) codes.Add(code);
        }
        return codes;
    }

    private Task<int> RecoveryCodesLeftAsync(Guid accountId, Guid userId, CancellationToken ct) =>
        Rows(accountId, userId).CountAsync(t => t.Name.StartsWith(RecoveryCodePrefix), ct);

    // A refused outcome writes nothing beyond a window rollover, or a spent
    // budget's missing window start.
    private async Task SaveIfChangedAsync(ApplicationUser user)
    {
        if (db.Entry(user).State == EntityState.Modified) await SaveAsync(user);
    }

    private async Task SaveAsync(ApplicationUser user)
    {
        var saved = await userManager.UpdateAsync(user);
        if (!saved.Succeeded)
            throw new InvalidOperationException("The locked user's save failed: " + string.Join(" ", saved.Errors.Select(e => e.Code)));
    }

    private void RequireTenant(Guid accountId)
    {
        if (ResolvedAccountId() != accountId)
            throw new InvalidOperationException("Enrolment runs under the resolved tenant of the user's farm.");
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

    private void RemoveRow(IdentityUserToken<Guid> row) => db.UserTokens.Remove(row);

    private IDataProtector Protector(Guid accountId, Guid userId) =>
        dataProtection.CreateProtector(ProtectorPurpose, accountId.ToString("D"), userId.ToString("D"));
}
