using System.Diagnostics;
using System.Security.Cryptography;
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
internal sealed partial class TwoFactorCredentials(
    AppDbContext db,
    IDataProtectionProvider dataProtection,
    TenantContext tenant,
    UserManager<ApplicationUser> userManager,
    TimeProvider timeProvider,
    AuthSecurityEventLogger securityEvents,
    IAccountRepository accounts,
    IPasskeyCredentials passkeys) : ITwoFactorCredentials, IOwnerGraceStart
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

    // The one read: untracked and unlocked, so the status route never waits
    // on a sign-in. Never a code, only the number left.
    public async Task<TwoFactorStatus> GetStatusAsync(Guid accountId, Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.AccountId == accountId)
            .Select(u => new { u.TwoFactorEnabled, u.SecondFactorRequiredSince })
            .SingleOrDefaultAsync(ct);
        var enabled = user?.TwoFactorEnabled == true;
        return new TwoFactorStatus(
            enabled, enabled ? await RecoveryCodesLeftAsync(accountId, userId, ct) : 0, user?.SecondFactorRequiredSince);
    }

    // #1281 (#1274 decision 18) — an Owner's grace start, set once. One guarded
    // statement, as SetLanguageAsync writes the row, so it takes only its row
    // lock and never joins a transaction. Rotating ConcurrencyStamp makes a
    // stale full-entity save fail its concurrency check instead of clearing the
    // value (G3.19).
    public async Task StartAsync(Guid accountId, Guid userId, DateTimeOffset now, CancellationToken ct) =>
        await db.Users
            .Where(u => u.Id == userId && u.AccountId == accountId && u.SecondFactorRequiredSince == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.SecondFactorRequiredSince, now)
                .SetProperty(u => u.ConcurrencyStamp, Guid.NewGuid().ToString()), ct);

    // #1274 "Verifying a code", steps 1 to 6; #1282 "Checking a passkey". A
    // refused passkey is logged by the sign-in finish that asked, at its exit.
    public async Task<FactorOutcome> VerifyAsync(
        Guid accountId, Guid userId, FactorProof proof, ProofBinding binding, CancellationToken ct)
    {
        var user = await UserRowLock.LoadLockedAsync(db, accountId, userId, ct);
        var kind = proof is FactorProof.Passkey ? FactorKind.Passkey : FactorKind.Authenticator;
        if (user is null || !Admits(user, binding, kind)) return new FactorOutcome.Superseded();

        // Charging a locked row would count the guess and re-lock the account.
        if (await userManager.IsLockedOutAsync(user))
            return new FactorOutcome.Rejected(Charged: false);

        return proof switch
        {
            FactorProof.Code { Value: SecondFactorCode.Totp totp } => await VerifyTotpAsync(accountId, user, totp, binding, ct),
            FactorProof.Code { Value: SecondFactorCode.Recovery recovery } => await VerifyRecoveryAsync(accountId, user, recovery, binding, ct),
            FactorProof.Passkey passkey => await VerifyPasskeyAsync(user, passkey, binding, ct),
            _ => throw new UnreachableException(),
        };
    }

    // Record 1282 "Checking a passkey", steps 3 to 5. A refusal is never
    // charged and never touches the daily budget (decision 8).
    private async Task<FactorOutcome> VerifyPasskeyAsync(
        ApplicationUser user, FactorProof.Passkey passkey, ProofBinding binding, CancellationToken ct)
    {
        if (passkey.Ceremony is not { } ceremony) return new FactorOutcome.Rejected(Charged: false);
        var outcome = await passkeys.AssertAsync(
            ceremony, user.Id, passkey.ExpectedCredentialId, passkey.CredentialJson, passkey.RequestHost, ct);
        return outcome is AssertionOutcome.Accepted accepted
            ? await AcceptAsync(user, binding, recoveryCodesLeft: null, accepted.Passkey)
            : new FactorOutcome.Rejected(Charged: false);
    }

    // Steps 3 to 6 for an authenticator code.
    private async Task<FactorOutcome> VerifyTotpAsync(
        Guid accountId, ApplicationUser user, SecondFactorCode.Totp totp, ProofBinding binding, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        if (user.SecondFactorFailuresSince is { } since && now - since > DailyWindow)
        {
            user.SecondFactorFailures = 0;
            user.SecondFactorFailuresSince = null;
        }
        if (user.SecondFactorFailures >= DailyBudget)
        {
            // A spent budget with no window start is not a state the writes
            // leave; it starts now, so it ends a day later.
            user.SecondFactorFailuresSince ??= now;
            await SaveIfChangedAsync(user);
            return new FactorOutcome.Throttled(user.SecondFactorFailuresSince.Value + DailyWindow - now);
        }

        // Only an authenticator code reads the key, so recovery codes keep
        // working when it cannot be read. A missing, undecryptable, short or
        // non-base32 key is unreadable: never a 500, and never a weak key.
        string? key;
        try
        {
            key = await ReadProtectedAsync(accountId, user.Id, ProtectedTwoFactorValue.AuthenticatorKey, ct);
        }
        catch (CryptographicException)
        {
            key = null;
        }
        if (!TotpMatcher.IsKey(key))
        {
            securityEvents.SecondFactorKeyUnreadable();
            await SaveIfChangedAsync(user);
            return new FactorOutcome.Unavailable();
        }

        var match = TotpMatcher.MatchStep(key, totp.Value, user.SecondFactorWatermark, now);
        if (match.Reused)
        {
            await SaveIfChangedAsync(user);
            return new FactorOutcome.Rejected(Charged: false);
        }
        if (match.Step is not { } step)
        {
            var outcome = await ChargeAsync(user);
            // After the lockout charge, as the record orders it. A UserManager
            // save, so it takes no request token and throws if it is lost.
            user.SecondFactorFailuresSince ??= now;
            user.SecondFactorFailures++;
            await SaveAsync(user);
            if (user.SecondFactorFailures == DailyBudget)
                securityEvents.SecondFactorBudgetSpent(user.Id);
            return outcome;
        }

        user.SecondFactorWatermark = step;
        return await AcceptAsync(user, binding, recoveryCodesLeft: null);
    }

    // Steps 4 to 6 for a recovery code, which never reads the key or the budget.
    private async Task<FactorOutcome> VerifyRecoveryAsync(
        Guid accountId, ApplicationUser user, SecondFactorCode.Recovery recovery, ProofBinding binding, CancellationToken ct)
    {
        // One DELETE that removes one row, so one code cannot be spent twice.
        var name = RecoveryCodeName(user.Id, recovery.Normalized);
        if (await Rows(accountId, user.Id).Where(t => t.Name == name).ExecuteDeleteAsync(ct) != 1)
            return await ChargeAsync(user);

        user.SecondFactorFailures = 0;
        user.SecondFactorFailuresSince = null;
        return await AcceptAsync(user, binding, await RecoveryCodesLeftAsync(accountId, user.Id, ct));
    }

    // Step 5's lockout charge, shared by both factors. AccessFailedAsync is a
    // UserManager save, which takes no request token.
    private async Task<FactorOutcome> ChargeAsync(ApplicationUser user)
    {
        securityEvents.SecondFactorFailed();
        if (await AccountLockout.RecordFailedAccessAsync(userManager, db, user))
            securityEvents.AccountLockedOut(user.Id);
        return new FactorOutcome.Rejected(Charged: true);
    }

    // Step 6.
    private async Task<FactorOutcome> AcceptAsync(
        ApplicationUser user, ProofBinding binding, int? recoveryCodesLeft, UserPasskeyInfo? passkey = null)
    {
        user.AccessFailedCount = 0;
        // Decision 2: every accepted login, by either factor, ends the challenge it used.
        if (binding.SignIns is not null)
            user.SecondFactorSignIns++;
        await SaveAsync(user);
        return new FactorOutcome.Accepted(recoveryCodesLeft, passkey);
    }
}
