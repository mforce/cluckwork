namespace Cluckwork.Application.Tests.Architecture;

internal static partial class RealModuleLedger
{
    internal static readonly CompatibilityException[] CompatibilityExceptions =
    [
        new("Cluckwork.Infrastructure.Repositories.UserRoleAssignmentRepository.ListByNameByUserAsync",
            "FlockManagement",
            ["Flocks"],
            "Access",
            "Left-joins a user's assignments to the filtered Flocks set in one statement, so each assigned flock carries its current name and the flock-scope filter decides which names the caller sees (#613). IFlockLookup.GetDisplayNamesAsync would add a second round trip; the assembly split must replace this remaining cross-module join while preserving its one-statement projection (#859).",
            "#859"),
    ];
}
