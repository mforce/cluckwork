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

    // Null means account-wide access: zero rows or any farm-wide row.
    // Throws when the tenant is unresolved, because the read would then find
    // no rows and grant account-wide access.
    Task<IReadOnlySet<Guid>?> GetAssignedFlocksAsync(
        Guid userId, CancellationToken ct = default);
}
