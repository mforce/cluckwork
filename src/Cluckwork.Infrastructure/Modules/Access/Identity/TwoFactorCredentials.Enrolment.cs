using System.Security.Cryptography;
using System.Text.Json;
using Cluckwork.Application.Modules.Access.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1274 "Enrolling and replacing a key": enrolment, recovery codes and the clear.
internal sealed partial class TwoFactorCredentials
{
    public async Task<EnrolmentStart> StartEnrolmentAsync(Guid accountId, Guid userId, CancellationToken ct)
    {
        RequireTenant(accountId);
        var user = await UserRowLock.LoadLockedAsync(db, accountId, userId, ct)
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
        var user = await UserRowLock.LoadLockedAsync(db, accountId, userId, ct);
        if (user is null) return new ConfirmOutcome.Superseded();

        var pendingRow = await Rows(accountId, userId)
            .SingleOrDefaultAsync(t => t.Name == ProtectedTwoFactorValue.PendingAuthenticator.RowName, ct);
        if (pendingRow is null) return new ConfirmOutcome.Superseded();

        PendingPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PendingPayload>(Protector(accountId, userId).Unprotect(pendingRow.Value!));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            payload = null;
        }
        // An undecryptable, undecodable or field-less payload ends the enrolment.
        if (payload is null || !TotpMatcher.IsKey(payload.Key) || payload.SecurityStampHash is null)
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

    // Decision 9: regenerating deletes the rows and inserts ten new ones. The
    // key, the flag and the epoch stay, so no session ends.
    public async Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(Guid accountId, Guid userId, CancellationToken ct)
    {
        RequireTenant(accountId);
        var user = await UserRowLock.LoadLockedAsync(db, accountId, userId, ct);
        if (user is null || !user.TwoFactorEnabled) return null;

        await Rows(accountId, userId).Where(t => t.Name.StartsWith(RecoveryCodePrefix)).ExecuteDeleteAsync(ct);
        var codes = NewRecoveryCodes();
        StageRecoveryCodes(userId, codes);
        await db.SaveChangesAsync(ct);
        return codes;
    }

    // #1274 "Enrolling and replacing a key": deletes every row without reading
    // a value. Only an enrolled user's clear is a factor change (decision 8);
    // ending sessions for an unenrolled user would cost them for nothing.
    public async Task<ClearOutcome> ClearAsync(Guid accountId, Guid userId, CancellationToken ct)
    {
        var user = await UserRowLock.LoadLockedAsync(db, accountId, userId, ct);
        if (user is null) return ClearOutcome.NotFound;

        var pendingDeleted = await Rows(accountId, userId)
            .Where(t => t.Name == ProtectedTwoFactorValue.PendingAuthenticator.RowName).ExecuteDeleteAsync(ct) > 0;
        await Rows(accountId, userId).ExecuteDeleteAsync(ct);
        if (!user.TwoFactorEnabled)
            return pendingDeleted ? ClearOutcome.PendingDeleted : ClearOutcome.NotEnrolled;

        user.TwoFactorEnabled = false;
        user.SecondFactorFailures = 0;
        user.SecondFactorFailuresSince = null;
        user.CredentialEpoch++;
        var saved = await userManager.UpdateSecurityStampAsync(user);
        if (!saved.Succeeded)
            throw new InvalidOperationException("The locked user's save failed: " + string.Join(" ", saved.Errors.Select(e => e.Code)));
        return ClearOutcome.Cleared;
    }

    // The pending enrolment, protected as one value: the new key, the epoch
    // and stamp it was started under, its issue time and its wrong confirms.
    private sealed record PendingPayload(
        string Key, int CredentialEpoch, string SecurityStampHash, DateTimeOffset IssuedAt, int WrongConfirms)
    {
        public override string ToString() => "[redacted]";
    }
}
