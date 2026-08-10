namespace Cluckwork.Application.Features.Users.DisableUser;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;

// #356 — disable a user. Step-up (#308) is required UNCONDITIONALLY, in both
// directions: unlike a role change, where demoting a Sales user to ReadOnly is
// ordinary administration, every disable revokes somebody's access outright.
// A caller can never self-target here — UserEndpoints refuses that at 400
// before this handler runs; the direct-DI race tests bypass that on purpose to
// reach the last-Owner guard.
// KNOWN, ACCEPTED CONSEQUENCE of validating here rather than inside the lock:
// the grant is single-use and is spent BEFORE IdentityProvider takes the
// account lock, so a retryable failure raised inside that lock — Users.Conflict
// (409) or Users.LastOwner (422) — leaves the caller with no grant, and
// resubmitting the identical request answers 403. Because #356 gates step-up
// unconditionally, this sits on the ordinary conflict path rather than on
// #355's rare promote-to-Owner one. Fixing it properly means validating the
// grant INSIDE the locked transaction, which pushes step-up knowledge into a
// layer that today knows nothing about it — the same trade-off #355 grilled and
// rejected. The 409 copy (IdentityProvider.ConcurrencyConflict) is worded to
// tell the Owner they must re-confirm, instead of promising a bare retry.
public sealed class DisableUserHandler(IIdentityProvider identity, IStepUpGrantService stepUp)
{
    public async Task<Result> HandleAsync(
        DisableUserCommand command, Guid accountId, Guid actingUserId, CancellationToken ct)
    {
        var proof = await stepUp.ValidateAsync(accountId, actingUserId, command.StepUpToken, ct);
        if (!proof.IsSuccess) return proof;

        var reason = string.IsNullOrWhiteSpace(command.Reason) ? null : command.Reason.Trim();

        return await identity.DisableUserAsync(accountId, command.UserId, actingUserId, reason, ct);
    }
}
