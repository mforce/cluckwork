using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1274 decision 11: the code half of an enrolled user's step-up and
// change-password, after CurrentPasswordCheck accepted the password. One
// sequence for both routes: parse, bind, the owned transaction, the code, the
// refused outcome's committed charge and the 400/429 answer (decision 12).
internal static class SecondFactorProof
{
    // onAccepted does the route's writes and returns its value, or a failure
    // that rolls the whole unit back. The unit commits after it, and after a
    // refused code too, so a client that hangs up cannot roll a charge back.
    // commitFailed maps a failed commit to an answer; without it the failure
    // propagates.
    internal static async Task<Result<T>> ProveAsync<T>(
        AppDbContext db, ITwoFactorCredentials twoFactor, Guid accountId, ApplicationUser user, string? code,
        Func<FactorOutcome.Accepted, CancellationToken, Task<Result<T>>> onAccepted,
        Func<Exception, Error>? commitFailed, CancellationToken ct)
    {
        // The SPA sends an empty field as no code.
        if (string.IsNullOrWhiteSpace(code))
            return Result.Failure<T>(SecondFactorErrors.Required);
        if (!SecondFactorCode.TryParse(code, out var parsed))
            return Result.Failure<T>(SecondFactorErrors.Incorrect);

        var binding = await BindAsync(db, user, ct);
        return await AmbientTransaction.RunOwnedAsync(db.Database, async (transaction, token) =>
        {
            var outcome = await twoFactor.VerifyAsync(accountId, user.Id, parsed, binding, token);
            Result<T> result;
            if (outcome is FactorOutcome.Accepted accepted)
            {
                result = await onAccepted(accepted, token);
                if (result.IsFailure) return result;
            }
            else
            {
                result = Result.Failure<T>(SecondFactorErrors.ForPrivilegedAction(outcome));
            }

            try
            {
                await transaction.CommitAsync(CancellationToken.None);
            }
            catch (Exception ex) when (commitFailed is not null)
            {
                return Result.Failure<T>(commitFailed(ex));
            }
            return result;
        }, ct);
    }

    // Decision 19: an accepted recovery code is audited with the codes left.
    internal static Task WriteRecoveryCodeUsedAsync(
        IAuditWriter audit, Guid userId, FactorOutcome.Accepted accepted, CancellationToken ct) =>
        accepted.RecoveryCodesLeft is { } remaining
            ? audit.WriteAsync(AuditActions.UserRecoveryCodeUsed, "User", userId,
                reason: null, details: new { remaining }, ct: ct)
            : Task.CompletedTask;

    // The epoch and stamp the password check read: the entry's original
    // values. A successful rehash save advanced the stamp, never the epoch; a
    // failed one advanced neither and leaves the user Modified, which the
    // user-row lock refuses, so it is reloaded. A writer that moved the row
    // meanwhile then fails the binding, and VerifyAsync answers Superseded.
    private static async Task<ProofBinding> BindAsync(AppDbContext db, ApplicationUser user, CancellationToken ct)
    {
        var entry = db.Entry(user);
        var binding = new ProofBinding(
            entry.Property(u => u.CredentialEpoch).OriginalValue,
            ProofBinding.HashStamp(entry.Property(u => u.SecurityStamp).OriginalValue),
            SignIns: null);
        if (entry.State == EntityState.Modified)
            await entry.ReloadAsync(ct);
        return binding;
    }
}
