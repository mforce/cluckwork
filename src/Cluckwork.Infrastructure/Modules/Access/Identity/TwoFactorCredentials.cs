using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Farm.Accounts;
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

// #1274 "The state it owns": the one owner of two-factor state, and the only
// code that checks an authenticator or recovery code. Every row is
// LoginProvider "Cluckwork", so Identity's own "[AspNetUserStore]" rows are
// never written. AspNetUsers is written only through the reloaded, locked user
// and a UserManager save, which checks and rotates ConcurrencyStamp, so a stale
// full-entity write cannot lower a watermark or a sign-in count (G3.19).
internal sealed class TwoFactorCredentials(
    AppDbContext db,
    IDataProtectionProvider dataProtection,
    TenantContext tenant,
    UserManager<ApplicationUser> userManager,
    TimeProvider timeProvider,
    AuthSecurityEventLogger securityEvents,
    IAccountRepository accounts) : ITwoFactorCredentials
{
    // Stored with every row or value: changing one makes every stored key
    // unreadable or orphaned.
    private const string LoginProvider = "Cluckwork";
    private const string ProtectorPurpose = "Cluckwork.TwoFactorCredentials";
    private const string RecoveryCodePrefix = "RecoveryCode:";

    private const string AccountIdProperty = nameof(ApplicationUser.AccountId);

    // Decision 6: a constant, not configuration.
    private const int DailyBudget = 20;
    private static readonly TimeSpan DailyWindow = TimeSpan.FromHours(24);

    private const int RecoveryCodeCount = 10;
    private const int MaxWrongConfirms = 5;
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);

    // #1274 "Verifying a code", steps 1 to 6.
    public async Task<FactorOutcome> VerifyAsync(
        Guid accountId, Guid userId, SecondFactorCode code, ProofBinding binding, CancellationToken ct)
    {
        var user = await LockAsync(accountId, userId, ct);
        if (user is null || user.DisabledAt is not null || !user.TwoFactorEnabled || user.CredentialEpoch < 1
            || user.CredentialEpoch != binding.CredentialEpoch
            || !string.Equals(ProofBinding.HashStamp(user.SecurityStamp), binding.SecurityStampHash, StringComparison.Ordinal)
            || (binding.SignIns is { } signIns && signIns != user.SecondFactorSignIns))
            return new FactorOutcome.Superseded();

        // Charging a locked row would count the guess and re-lock the account.
        if (await userManager.IsLockedOutAsync(user))
            return new FactorOutcome.Rejected(Charged: false);

        var now = timeProvider.GetUtcNow();
        long? matchedStep = null;
        bool matched;
        if (code is SecondFactorCode.Totp totp)
        {
            if (user.SecondFactorFailuresSince is { } since && now - since > DailyWindow)
            {
                user.SecondFactorFailures = 0;
                user.SecondFactorFailuresSince = null;
                await SaveAsync(user);
            }
            if (user.SecondFactorFailures >= DailyBudget)
                return new FactorOutcome.Throttled(user.SecondFactorFailuresSince!.Value + DailyWindow - now);

            // Only a TOTP code reads the key, so recovery codes keep working
            // when it cannot be decrypted.
            string? key;
            try
            {
                key = await ReadProtectedAsync(accountId, userId, ProtectedTwoFactorValue.AuthenticatorKey, ct);
            }
            catch (CryptographicException)
            {
                securityEvents.SecondFactorKeyUnreadable();
                return new FactorOutcome.Unavailable();
            }
            if (key is null) return new FactorOutcome.Unavailable();

            var match = TotpMatcher.MatchStep(key, totp.Value, user.SecondFactorWatermark, now);
            if (match.Reused) return new FactorOutcome.Rejected(Charged: false);
            matchedStep = match.Step;
            matched = matchedStep is not null;
        }
        else
        {
            // One DELETE that removes one row, so one code cannot be spent twice.
            var name = RecoveryCodeName(userId, ((SecondFactorCode.Recovery)code).Normalized);
            matched = await Rows(accountId, userId).Where(t => t.Name == name).ExecuteDeleteAsync(ct) == 1;
        }

        if (!matched)
        {
            securityEvents.SecondFactorFailed();
            if (await AccountLockout.RecordFailedAccessAsync(userManager, db, user))
                securityEvents.AccountLockedOut(user.Id);
            if (code is SecondFactorCode.Totp)
            {
                user.SecondFactorFailuresSince ??= now;
                user.SecondFactorFailures++;
                await SaveAsync(user);
                if (user.SecondFactorFailures == DailyBudget)
                    securityEvents.SecondFactorBudgetSpent(user.Id);
            }
            return new FactorOutcome.Rejected(Charged: true);
        }

        user.AccessFailedCount = 0;
        if (matchedStep is { } step)
        {
            user.SecondFactorWatermark = step;
        }
        else
        {
            user.SecondFactorFailures = 0;
            user.SecondFactorFailuresSince = null;
        }
        // Decision 2: every accepted login, by either factor, ends the challenge it used.
        if (binding.SignIns is not null)
            user.SecondFactorSignIns++;
        await SaveAsync(user);
        return new FactorOutcome.Accepted(matchedStep is null ? await RecoveryCodesLeftAsync(accountId, userId, ct) : null);
    }

    public async Task<EnrolmentStart> StartEnrolmentAsync(Guid accountId, Guid userId, CancellationToken ct)
    {
        RequireTenant(accountId);
        var user = await LockAsync(accountId, userId, ct)
            ?? throw new InvalidOperationException("Enrolment starts only for a user of the resolved farm.");
        var account = await accounts.GetCurrentAsync(ct)
            ?? throw new InvalidOperationException("Enrolment starts only under a resolved farm.");

        // A second start replaces the payload; the live key is never touched.
        var key = userManager.GenerateNewAuthenticatorKey();
        var payload = new PendingPayload(
            key, user.CredentialEpoch, ProofBinding.HashStamp(user.SecurityStamp), timeProvider.GetUtcNow(), WrongConfirms: 0);
        await StageProtectedAsync(userId, ProtectedTwoFactorValue.PendingAuthenticator, JsonSerializer.Serialize(payload), ct);
        await db.SaveChangesAsync(ct);

        return new EnrolmentStart(key, OtpauthUri.Build(account.Name, account.Slug, user.Email ?? string.Empty, userId, key));
    }

    public async Task<ConfirmOutcome> ConfirmEnrolmentAsync(
        Guid accountId, Guid userId, SecondFactorCode.Totp code, CancellationToken ct)
    {
        RequireTenant(accountId);
        var user = await LockAsync(accountId, userId, ct);
        if (user is null) return new ConfirmOutcome.Superseded();

        var pendingRow = await Rows(accountId, userId)
            .SingleOrDefaultAsync(t => t.Name == ProtectedTwoFactorValue.PendingAuthenticator.RowName, ct);
        if (pendingRow is null) return new ConfirmOutcome.Superseded();

        PendingPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<PendingPayload>(Protector(accountId, userId).Unprotect(pendingRow.Value!))!;
        }
        catch (CryptographicException)
        {
            RemoveRow(pendingRow);
            await db.SaveChangesAsync(ct);
            securityEvents.SecondFactorKeyUnreadable();
            return new ConfirmOutcome.Superseded();
        }

        var now = timeProvider.GetUtcNow();
        if (now - payload.IssuedAt > PendingLifetime
            || payload.CredentialEpoch != user.CredentialEpoch
            || !string.Equals(payload.SecurityStampHash, ProofBinding.HashStamp(user.SecurityStamp), StringComparison.Ordinal))
            return new ConfirmOutcome.Superseded();

        // The payload is single-use, so the match ignores the watermark.
        if (TotpMatcher.MatchStep(payload.Key, code.Value, long.MinValue, now).Step is not { } step)
        {
            // Not charged to the lockout; the fifth wrong confirm ends the
            // enrolment, so a stolen session cannot search the code space.
            var wrong = payload.WrongConfirms + 1;
            if (wrong >= MaxWrongConfirms)
                RemoveRow(pendingRow);
            else
                pendingRow.Value = Protector(accountId, userId).Protect(JsonSerializer.Serialize(payload with { WrongConfirms = wrong }));
            await db.SaveChangesAsync(ct);
            return new ConfirmOutcome.Rejected(MaxWrongConfirms - wrong);
        }

        await StageProtectedAsync(userId, ProtectedTwoFactorValue.AuthenticatorKey, payload.Key, ct);
        RemoveRow(pendingRow);
        var recoveryCodes = user.TwoFactorEnabled ? null : NewRecoveryCodes();
        if (recoveryCodes is not null)
            StageRecoveryCodes(userId, recoveryCodes);

        // Decision 8: a factor change ends every session and connected app
        // minted before it. The caller revokes and mints the fresh pair.
        user.TwoFactorEnabled = true;
        user.SecondFactorWatermark = Math.Max(user.SecondFactorWatermark, step);
        user.CredentialEpoch++;
        var saved = await userManager.UpdateSecurityStampAsync(user);
        if (!saved.Succeeded)
            throw new InvalidOperationException("The locked user's save failed: " + string.Join(" ", saved.Errors.Select(e => e.Code)));

        return recoveryCodes is not null ? new ConfirmOutcome.Enrolled(recoveryCodes) : new ConfirmOutcome.Replaced();
    }

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
    // malformed input never reaches here; the length check fails closed if a
    // caller skips it.
    internal static string RecoveryCodeName(Guid userId, string code)
    {
        var compact = code.Replace(" ", "").Replace("-", "").ToUpperInvariant();
        if (compact.Length != SecondFactorCode.RecoveryLength)
            throw new ArgumentException("A recovery code has 16 characters.", nameof(code));
        var normalized = string.Join('-', compact.Chunk(4).Select(c => new string(c)));
        return RecoveryCodePrefix + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:D}:{normalized}")));
    }

    // Distinct by construction: every code is already in its normal form.
    private static List<string> NewRecoveryCodes()
    {
        var codes = new List<string>(RecoveryCodeCount);
        while (codes.Count < RecoveryCodeCount)
        {
            var raw = RandomNumberGenerator.GetString(SecondFactorCode.RecoveryAlphabet, SecondFactorCode.RecoveryLength);
            var code = string.Join('-', raw.Chunk(4).Select(c => new string(c)));
            if (!codes.Contains(code)) codes.Add(code);
        }
        return codes;
    }

    private Task<int> RecoveryCodesLeftAsync(Guid accountId, Guid userId, CancellationToken ct) =>
        Rows(accountId, userId).CountAsync(t => t.Name.StartsWith(RecoveryCodePrefix), ct);

    // "Locks" in #1274 "The state it owns": the user-row lock holds until the
    // caller commits, so every check and update here is exact under
    // concurrency. A tracked query returns the identity-map instance with the
    // values it read before the lock, hence the reload; the reload would
    // discard a caller's unsaved change, hence the throw.
    private async Task<ApplicationUser?> LockAsync(Guid accountId, Guid userId, CancellationToken ct)
    {
        if (db.ChangeTracker.Entries<ApplicationUser>().Any(e => e.Entity.Id == userId && e.State == EntityState.Modified))
            throw new InvalidOperationException("Save the user's tracked changes before a two-factor write; the lock reloads the row.");
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Two-factor writes run inside the caller's transaction, which holds the lock.");

        await UserRowLock.TakeAsync(db, accountId, userId, ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.AccountId == accountId, ct);
        if (user is not null)
            await db.Entry(user).ReloadAsync(ct);
        return user;
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

    // The pending enrolment, protected as one value: the new key, the epoch
    // and stamp it was started under, its issue time and its wrong confirms.
    private sealed record PendingPayload(
        string Key, int CredentialEpoch, string SecurityStampHash, DateTimeOffset IssuedAt, int WrongConfirms);
}
