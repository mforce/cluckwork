using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1284 (record 1282 decisions 10, 15, 16): the caller's own passkeys, and an
// Owner's removal of another user's. Every write runs outside IdempotentUnit,
// under /api/v1/auth/passkeys on ResponseNotCacheable, except the Owner's
// removal, which runs inside it as the two-factor reset does.
public sealed partial class IdentityProvider
{
    private static readonly Error PasskeyRefused =
        new(PasskeyErrorCodes.Refused, "That passkey did not work. Try again.");

    private static readonly Error PasskeyLimitReached =
        new(PasskeyErrorCodes.LimitReached, $"You already have {PasskeyCredentials.MaxPasskeys} passkeys. Remove one to add another.");

    private static readonly Error PasskeyNotFound =
        new(PasskeyErrorCodes.NotFound, "That passkey was not found.");

    public Task<PasskeyCeremonyStart> StartPasskeyRegistrationAsync(
        Guid accountId, Guid userId, string requestHost, CancellationToken ct = default) =>
        passkeys.StartRegistrationAsync(accountId, userId, requestHost, ct);

    // Decision 3: the ceremony is consumed by the transaction's first
    // statement, and every refusal after it commits, so it is good for one
    // attempt. The grant was spent before (decision 15), so the acting
    // session is compared under the user-row lock, as turn-off does.
    public Task<Result<PasskeyRegistration>> RegisterPasskeyAsync(
        Guid accountId, Guid userId, int credentialEpoch, Guid ceremony, string credentialJson, string name,
        string requestHost, CancellationToken ct = default) =>
        AmbientTransaction.RunOwnedAsync(db.Database, async (transaction, token) =>
        {
            // A refusal keeps the consumed ceremony, so it commits too.
            async Task<Result<PasskeyRegistration>> RefuseAsync(Error error)
            {
                await transaction.CommitAsync(CancellationToken.None);
                return Result.Failure<PasskeyRegistration>(error);
            }

            var consumed = await passkeys.ConsumeAsync(ceremony, CeremonyPurpose.Registration, userId, token);
            if (!await ActingSessionIsCurrentAsync(accountId, userId, credentialEpoch, token))
                return await RefuseAsync(CredentialsSuperseded);
            if (consumed is null)
            {
                securityEvents.PasskeyFailed();
                return await RefuseAsync(PasskeyRefused);
            }

            switch (await passkeys.RegisterAsync(consumed, accountId, userId, credentialJson, name, requestHost, token))
            {
                case RegisterOutcome.LimitReached:
                    return await RefuseAsync(PasskeyLimitReached);
                case RegisterOutcome.Registered registered:
                    var tokens = registered.FirstFactor ? await ReissueActingSessionAsync(accountId, userId, token) : null;
                    await audit.WriteAsync(AuditActions.UserPasskeyRegistered, "User", userId,
                        reason: null, details: new { firstFactor = registered.FirstFactor }, ct: token);
                    await db.SaveChangesAsync(token);
                    await transaction.CommitAsync(token);
                    securityEvents.PasskeyRegistered(userId);
                    return Result.Success(new PasskeyRegistration(registered.Passkey, tokens));
                default:
                    return await RefuseAsync(PasskeyRefused);
            }
        }, ct);

    public Task<IReadOnlyList<PasskeySummary>> ListPasskeysAsync(Guid accountId, Guid userId, CancellationToken ct = default) =>
        passkeys.ListAsync(accountId, userId, ct);

    public Task<Result> RenamePasskeyAsync(
        Guid accountId, Guid userId, byte[] credentialId, string name, CancellationToken ct = default) =>
        AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            if (!await passkeys.RenameAsync(accountId, userId, credentialId, name, token))
                return Result.Failure(PasskeyNotFound);

            await audit.WriteAsync(AuditActions.UserPasskeyRenamed, "User", userId, reason: null, details: null, ct: token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Result.Success();
        }, ct);

    // Decision 10: the grant was spent before, so the acting session is
    // compared under the lock; a removal ends every other session and
    // connected app, and this one gets a fresh pair.
    public Task<Result<TokenPair>> RemovePasskeyAsync(
        Guid accountId, Guid userId, int credentialEpoch, byte[] credentialId, CancellationToken ct = default) =>
        AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            if (!await ActingSessionIsCurrentAsync(accountId, userId, credentialEpoch, token))
                return Result.Failure<TokenPair>(CredentialsSuperseded);
            if (!await passkeys.RemoveAsync(accountId, userId, credentialId, token))
                return Result.Failure<TokenPair>(PasskeyNotFound);

            var pair = await ReissueActingSessionAsync(accountId, userId, token);
            await audit.WriteAsync(AuditActions.UserPasskeyRemoved, "User", userId, reason: null, details: null, ct: token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            securityEvents.PasskeyRemoved(userId);
            return Result.Success(pair);
        }, ct);

    // Decision 10: the two-factor reset's order, the account lock and then the
    // actor re-check, before RemoveAllAsync writes anything.
    public Task<Result> RemoveUserPasskeysAsync(
        Guid accountId, Guid userId, Guid actingUserId, CancellationToken ct = default) =>
        AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            var lockedAccount = await accounts.GetCurrentLockedAsync(token);
            if (lockedAccount is null || lockedAccount.Id != accountId)
                return Result.Failure(Error.NotFound("Accounts", accountId));

            var actor = await RequireActiveOwnerAsync(accountId, actingUserId, token);
            if (actor.IsFailure)
                return actor;

            if (await passkeys.RemoveAllAsync(accountId, userId, token) is not { } count)
                return Result.Failure(Error.NotFound("Users", userId));
            if (count > 0)
            {
                await RevokeAllActiveForUserAsync(userId, timeProvider.GetUtcNow(), token);
                await audit.WriteAsync(AuditActions.UserPasskeysRemovedByOwner, "User", userId,
                    reason: null, details: new { count }, ct: token);
            }
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Result.Success();
        }, ct);

    // Decision 10: the clear-passkeys verb, for one farm in a fresh scope. The
    // audit writer refuses a row without a tenant and an actor, so both are
    // resolved here, as recover-admin does for its target.
    public Task<int> ClearPasskeysAsync(Guid accountId, string? reason, CancellationToken ct = default)
    {
        tenant.Resolve(accountId);
        currentUser.ResolveSystemActor(SystemActors.ClearPasskeys);
        return AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            var cleared = 0;
            var now = timeProvider.GetUtcNow();
            foreach (var userId in await passkeys.UsersWithPasskeysAsync(accountId, token))
            {
                var count = await passkeys.RemoveAllAsync(accountId, userId, token) ?? 0;
                if (count == 0) continue;
                await RevokeAllActiveForUserAsync(userId, now, token);
                await audit.WriteAsync(AuditActions.UserPasskeysCleared, "User", userId,
                    reason, details: new { count }, ct: token);
                cleared++;
            }
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return cleared;
        }, ct);
    }
}
