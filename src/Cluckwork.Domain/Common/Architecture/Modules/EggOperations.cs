namespace Cluckwork.Domain.Common.Architecture;

[ModuleOwner("EggOperations", "module",
    Namespaces = [
        "Cluckwork.Domain.Eggs",
        "Cluckwork.Application.Features.DailyEntries",
        "Cluckwork.Application.Features.EggGrades",
        "Cluckwork.Application.Features.EggLots",
        "Cluckwork.Application.Features.Eggs",
    ],
    Implementations = [
        "Cluckwork.Infrastructure.Repositories.EggOperationsFixture",
        "Cluckwork.Infrastructure.Repositories.DailyEntryRepository",
        "Cluckwork.Infrastructure.Repositories.EggGradeRepository",
        "Cluckwork.Infrastructure.Repositories.EggInventoryMovementRepository",
        "Cluckwork.Infrastructure.Repositories.EggLotRepository",
    ])]
[ModuleEdge(
    "EggOperations", "Farm", "R",
    "CreateEggGradeHandler attaches a new grade to Domain.Accounts.SeedDefaults.FarmId, the single-farm stand-in for the farm the grade belongs to; EggGradeFloorPolicy resolves the caller's effective role through Domain.Accounts.Roles to decide whether a grade's low-stock floor may move (#911, Owner-only per #729). Design 3.4 row Egg Ops -> Farm = R.",
    "Cluckwork.Application.Features.EggGrades.CreateEggGrade.CreateEggGradeHandler",
    "Cluckwork.Application.Features.EggGrades.EggGradeFloorPolicy")]
[ModuleEdge(
    "EggOperations", "FlockManagement", "W",
    "Daily entry is the mortality writer: SubmitDailyEntryHandler, AdjustDailyEntryHandler and VoidDailyEntryHandler append bird-movement rows through Flock Management's IMortalityLedger port, which adds the row to the caller's unit of work and never saves, so it commits with the entry itself (#54, #69, #852). Every daily-entry handler reads the flock through the IFlockLookup port to check CanRecordProductionOn for the entry's date. Design 3.4 row Egg Ops -> Flock = W.",
    "Cluckwork.Application.Features.DailyEntries.AdjustDailyEntry.AdjustDailyEntryHandler",
    "Cluckwork.Application.Features.DailyEntries.RecordDailyEntry.RecordDailyEntryHandler",
    "Cluckwork.Application.Features.DailyEntries.SubmitDailyEntry.SubmitDailyEntryHandler",
    "Cluckwork.Application.Features.DailyEntries.VoidDailyEntry.VoidDailyEntryHandler")]
internal static class EggOperationsModuleRules { }
