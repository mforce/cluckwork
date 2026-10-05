namespace Cluckwork.Domain.Common.Architecture;

[ModuleOwner("FlockManagement", "module",
    Namespaces = [
        "Cluckwork.Domain.Modules.FlockManagement",
        "Cluckwork.Application.Modules.FlockManagement",
    ],
    Implementations = [
        "Cluckwork.Infrastructure.Repositories.BirdMovementRepository",
        "Cluckwork.Infrastructure.Repositories.FlockFixture",
        "Cluckwork.Infrastructure.Repositories.FlockRepository",
    ])]
[ModuleEdge(
    "FlockManagement", "Farm", "R",
    "CreateFlockHandler places a new flock on Domain.Accounts.SeedDefaults.FarmId and SeedDefaults.HouseId, the single-farm stand-ins. Design 3.4 row Flock -> Farm = R.",
    "Cluckwork.Application.Modules.FlockManagement.Flocks.CreateFlock.CreateFlockHandler")]
internal static class FlockManagementModuleRules { }
