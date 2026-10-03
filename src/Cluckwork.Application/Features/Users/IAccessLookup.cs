namespace Cluckwork.Application.Features.Users;

// #857 — what peer modules need from Access about an actor. Both reads are
// fresh, on the caller's scoped context, so a caller inside a transaction reads
// inside it: ConfirmSaleHandler reads the role and the assignments after its
// account and order locks (#612, #727). Nothing here is cached.
public interface IAccessLookup
{
    // Null when the user is not an ACTIVE member of the account (#612).
    Task<Cluckwork.Domain.Accounts.EffectiveAccountRole?> GetEffectiveRoleAsync(
        Guid accountId, Guid userId, CancellationToken ct = default);

    // No rows means account-wide access; a null FlockId is a farm-wide row.
    Task<IReadOnlyList<FlockAssignmentDetails>> ListFlockAssignmentsAsync(
        Guid userId, CancellationToken ct = default);
}

public sealed record FlockAssignmentDetails(Guid Id, Guid? FlockId);
