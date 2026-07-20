namespace Cluckwork.Domain.Accounts;

// Spec §5.2 user_role_assignments — the scoping dimension on top of the
// role itself. Phase 1: only flock-level worker scoping is enforced; FarmId
// and HouseId exist so the schema doesn't churn when farm/house management
// become real (spec defers them). A worker with NO assignment rows keeps
// account-wide production access (grandfathering #73 workers); adding the
// first row narrows them to the assigned flocks.
public sealed class UserRoleAssignment : AggregateRoot<Guid>
{
    public Guid UserId { get; private set; }
    public Guid? FarmId { get; private set; }
    public Guid? HouseId { get; private set; }
    public Guid? FlockId { get; private set; }

    private UserRoleAssignment() { }

    public static UserRoleAssignment Create(
        Guid id, Guid accountId, Guid userId, Guid? farmId, Guid? houseId, Guid? flockId)
    {
        if (farmId is null && houseId is null && flockId is null)
            throw new ArgumentException("An assignment must scope to a farm, house, or flock.");

        return new UserRoleAssignment
        {
            Id = id, AccountId = accountId, UserId = userId,
            FarmId = farmId, HouseId = houseId, FlockId = flockId,
        };
    }
}
