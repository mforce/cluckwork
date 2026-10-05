namespace Cluckwork.Application.Features.Users;

// Simulation fixture write; registered beside the seeders outside Production.
// The trusted seeder holds no step-up grant, so this skips the interactive
// AssignFlockHandler's step-up and Worker-role checks, and nothing else.
[ModuleContract("Access")]
public interface IAccessFixture
{
    // Assigns the flock to the farm's user unless already assigned, writes the
    // acting user's User.FlockAssign audit row with the flock's name, and saves
    // once. Throws when the user or the flock is not in accountId's farm.
    Task EnsureFlockAssignmentAsync(
        Guid accountId, Guid userId, string email, Guid flockId, CancellationToken ct = default);
}
