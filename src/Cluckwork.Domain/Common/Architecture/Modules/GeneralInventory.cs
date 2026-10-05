namespace Cluckwork.Domain.Common.Architecture;

[ModuleOwner("GeneralInventory", "module",
    Namespaces = [
        "Cluckwork.Domain.Inventory",
        "Cluckwork.Application.Features.Inventory",
    ],
    Implementations = [
        "Cluckwork.Infrastructure.Repositories.FeedUsageRepository",
        "Cluckwork.Infrastructure.Repositories.InventoryFixture",
        "Cluckwork.Infrastructure.Repositories.InventoryItemRepository",
        "Cluckwork.Infrastructure.Repositories.InventoryLotRepository",
        "Cluckwork.Infrastructure.Repositories.InventoryMovementRepository",
        "Cluckwork.Infrastructure.Repositories.WaterUsageRepository",
    ])]
[ModuleEdge(
    "GeneralInventory", "EggOperations", "R",
    "RecordFeedUsageHandler and RecordWaterUsageHandler inject Egg Operations' IDailyEntryLookup port by fully qualified name and call FindIdForFlockScopedWriteAsync (#853), so a feed or water record carries the day's daily-entry provenance. Design 3.4 shows Inventory -> Egg Ops as none; this is live coupling the target design has still to remove.",
    "Cluckwork.Application.Features.Inventory.RecordFeedUsage.RecordFeedUsageHandler",
    "Cluckwork.Application.Features.Inventory.RecordWaterUsage.RecordWaterUsageHandler")]
[ModuleEdge(
    "GeneralInventory", "Farm", "R",
    "CreateInventoryItemHandler, RecordPurchaseHandler and UpdateInventoryItemHandler inject Farm's IAccountRepository for the farm currency a priced item or a purchase snapshots, under the same FOR SHARE account-row lock as the sales path (#162), and CreateInventoryItemHandler attaches the item to Domain.Accounts.SeedDefaults.FarmId. Design 3.4 row Inventory -> Farm = R.",
    "Cluckwork.Application.Features.Inventory.CreateInventoryItem.CreateInventoryItemHandler",
    "Cluckwork.Application.Features.Inventory.RecordPurchase.RecordPurchaseHandler",
    "Cluckwork.Application.Features.Inventory.UpdateInventoryItem.UpdateInventoryItemHandler")]
[ModuleEdge(
    "GeneralInventory", "FlockManagement", "R",
    "RecordFeedUsageHandler, RecordWaterUsageHandler and UpdateWaterUsageHandler inject Flock Management's IFlockLookup port to prove the flock being fed or watered exists and is eligible for the usage date. Design 3.4 row Inventory -> Flock = R.",
    "Cluckwork.Application.Features.Inventory.RecordFeedUsage.RecordFeedUsageHandler",
    "Cluckwork.Application.Features.Inventory.RecordWaterUsage.RecordWaterUsageHandler",
    "Cluckwork.Application.Features.Inventory.UpdateWaterUsage.UpdateWaterUsageHandler")]
internal static class GeneralInventoryModuleRules { }
