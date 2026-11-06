using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1274 "Enrolling and replacing a key": the caller's own enrolment. Each
// write runs in its own transaction outside IdempotentUnit, because a wrong
// confirm's count must commit with its 400.
public sealed partial class IdentityProvider
{
    private static readonly Error EnrolmentExpired =
        new(EnrolmentErrorCodes.Expired, "This setup has expired. Start again.");

    private static readonly Error TwoFactorNotEnabled =
        new(EnrolmentErrorCodes.NotEnabled, "Two-factor authentication is not on for this account.");

    public Task<TwoFactorStatus> GetTwoFactorStatusAsync(Guid accountId, Guid userId, CancellationToken ct = default) =>
        twoFactor.GetStatusAsync(accountId, userId, ct);

    public Task<Result<EnrolmentStart>> StartTwoFactorEnrolmentAsync(
        Guid accountId, Guid userId, int credentialEpoch, CancellationToken ct = default) =>
        AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            if (!await ActingSessionIsCurrentAsync(accountId, userId, credentialEpoch, token))
                return Result.Failure<EnrolmentStart>(CredentialsSuperseded);

            var start = await twoFactor.StartEnrolmentAsync(accountId, userId, token);
            await transaction.CommitAsync(token);
            securityEvents.TwoFactorEnrolmentStarted(userId);
            return Result.Success(start);
        }, ct);

    public Task<Result<EnrolmentConfirmation>> ConfirmTwoFactorEnrolmentAsync(
        Guid accountId, Guid userId, string? code, CancellationToken ct = default)
    {
        // A malformed code cannot be a guess, so it counts nothing (decision 5).
        if (!SecondFactorCode.TryParse(code, out var parsed) || parsed is not SecondFactorCode.Totp totp)
            return Task.FromResult(Result.Failure<EnrolmentConfirmation>(new EnrolmentCodeIncorrectError(AttemptsLeft: null)));

        // A code check, so it owns its transaction: a joined caller could roll
        // the wrong-confirm count back.
        return AmbientTransaction.RunOwnedAsync(db.Database, async (transaction, token) =>
        {
            var outcome = await twoFactor.ConfirmEnrolmentAsync(accountId, userId, totp, token);
            if (outcome is ConfirmOutcome.Rejected or ConfirmOutcome.Superseded)
            {
                // The wrong-confirm count, or the deletion of an unreadable
                // payload, stands, so a client that hangs up cannot undo it.
                await transaction.CommitAsync(CancellationToken.None);
                return Result.Failure<EnrolmentConfirmation>(outcome is ConfirmOutcome.Rejected rejected
                    ? new EnrolmentCodeIncorrectError(rejected.AttemptsLeft)
                    : EnrolmentExpired);
            }

            // Decision 8: every other session and connected app ends, and this
            // one gets a fresh pair from the new epoch. The new key's code was
            // the second factor, so the lineage starts now (G3.31).
            var recoveryCodes = (outcome as ConfirmOutcome.Enrolled)?.RecoveryCodes;
            var user = await db.Users.SingleAsync(u => u.Id == userId && u.AccountId == accountId, token);
            var now = timeProvider.GetUtcNow();
            await RevokeAllActiveForUserAsync(user.Id, now, token);
            var (rawToken, tokenHash) = GenerateRefreshToken();
            db.RefreshTokens.Add(NewToken(user, tokenHash, signedInAt: now));
            await audit.WriteAsync(AuditActions.UserTwoFactorEnabled, "User", user.Id,
                reason: null, details: new { replaced = recoveryCodes is null }, ct: token);
            await db.SaveChangesAsync(token);

            var roles = await userManager.GetRolesAsync(user);
            var pair = jwtTokenService.CreateTokenPair(user, [.. roles], rawToken);
            await transaction.CommitAsync(token);
            securityEvents.TwoFactorEnrolled(user.Id);
            return Result.Success(new EnrolmentConfirmation(pair, recoveryCodes));
        }, ct);
    }

    public Task<Result<IReadOnlyList<string>>> RegenerateRecoveryCodesAsync(
        Guid accountId, Guid userId, int credentialEpoch, CancellationToken ct = default) =>
        AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            if (!await ActingSessionIsCurrentAsync(accountId, userId, credentialEpoch, token))
                return Result.Failure<IReadOnlyList<string>>(CredentialsSuperseded);
            if (await twoFactor.RegenerateRecoveryCodesAsync(accountId, userId, token) is not { } codes)
                return Result.Failure<IReadOnlyList<string>>(TwoFactorNotEnabled);

            await audit.WriteAsync(AuditActions.UserRecoveryCodesRegenerated, "User", userId,
                reason: null, details: null, ct: token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Result.Success(codes);
        }, ct);
}
