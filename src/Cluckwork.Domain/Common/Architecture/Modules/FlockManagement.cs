namespace Cluckwork.Domain.Common.Architecture;

[ModuleOwner("FlockManagement", "module",
    Namespaces = [
        "Cluckwork.Domain.Modules.FlockManagement",
        "Cluckwork.Application.Modules.FlockManagement",
        "Cluckwork.Infrastructure.Modules.FlockManagement",
    ])]
[ModuleEdge(
    "FlockManagement", "EggOperations", "R",
    "BirdMovementConfiguration declares FK_BirdMovements_DailyEntries_DailyEntryId, the foreign key from a mortality movement to the daily entry it came from, with HasOne<DailyEntry>. It is schema only: daily-entry handlers append movements through Flock Management's IMortalityLedger port (#852), and no other Flock Management code references Egg Operations. The cell exists because the configuration moved into the module (#1087 S9).",
    "Cluckwork.Infrastructure.Modules.FlockManagement.Configurations.BirdMovementConfiguration")]
[ModuleEdge(
    "FlockManagement", "Farm", "R",
    "CreateFlockHandler places a new flock on Domain.Accounts.SeedDefaults.FarmId and SeedDefaults.HouseId, the single-farm stand-ins. Design 3.4 row Flock -> Farm = R.",
    "Cluckwork.Application.Modules.FlockManagement.Flocks.CreateFlock.CreateFlockHandler")]
internal static class FlockManagementModuleRules { }
