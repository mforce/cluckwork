namespace Cluckwork.Domain.Common.Architecture;

[ModuleOwner("GeneralInventory", "module",
    Namespaces = [
        "Cluckwork.Domain.Modules.GeneralInventory",
        "Cluckwork.Application.Modules.GeneralInventory",
        "Cluckwork.Infrastructure.Modules.GeneralInventory",
    ])]
[ModuleEdge(
    "GeneralInventory", "EggOperations", "R",
    "RecordFeedUsageHandler and RecordWaterUsageHandler inject Egg Operations' IDailyEntryLookup port by fully qualified name and call FindIdForFlockScopedWriteAsync (#853), so a feed or water record carries the day's daily-entry provenance. Design 3.4 shows Inventory -> Egg Ops as none; this is live coupling the target design has still to remove. FeedUsageConfiguration and WaterUsageConfiguration declare the usage rows' foreign keys to DailyEntry (#1087 S9).",
    "Cluckwork.Application.Modules.GeneralInventory.Inventory.RecordFeedUsage.RecordFeedUsageHandler",
    "Cluckwork.Application.Modules.GeneralInventory.Inventory.RecordWaterUsage.RecordWaterUsageHandler",
    "Cluckwork.Infrastructure.Modules.GeneralInventory.Configurations.FeedUsageConfiguration",
    "Cluckwork.Infrastructure.Modules.GeneralInventory.Configurations.WaterUsageConfiguration")]
[ModuleEdge(
    "GeneralInventory", "Farm", "R",
    "CreateInventoryItemHandler, RecordPurchaseHandler and UpdateInventoryItemHandler inject Farm's IAccountRepository for the farm currency a priced item or a purchase snapshots, under the same FOR SHARE account-row lock as the sales path (#162), and CreateInventoryItemHandler attaches the item to Domain.Accounts.SeedDefaults.FarmId. Design 3.4 row Inventory -> Farm = R. FeedUsageRepository, InventoryItemRepository and InventoryLotRepository implement Farm's ICurrencyBoundRowSource, so Farm's currency probe can ask whether their tables hold farm-currency amounts (#1087 S9).",
    "Cluckwork.Application.Modules.GeneralInventory.Inventory.CreateInventoryItem.CreateInventoryItemHandler",
    "Cluckwork.Application.Modules.GeneralInventory.Inventory.RecordPurchase.RecordPurchaseHandler",
    "Cluckwork.Application.Modules.GeneralInventory.Inventory.UpdateInventoryItem.UpdateInventoryItemHandler",
    "Cluckwork.Infrastructure.Modules.GeneralInventory.Repositories.FeedUsageRepository",
    "Cluckwork.Infrastructure.Modules.GeneralInventory.Repositories.InventoryItemRepository",
    "Cluckwork.Infrastructure.Modules.GeneralInventory.Repositories.InventoryLotRepository")]
[ModuleEdge(
    "GeneralInventory", "FlockManagement", "R",
    "RecordFeedUsageHandler, RecordWaterUsageHandler and UpdateWaterUsageHandler inject Flock Management's IFlockLookup port to prove the flock being fed or watered exists and is eligible for the usage date. Design 3.4 row Inventory -> Flock = R. FeedUsageConfiguration, InventoryMovementConfiguration and WaterUsageConfiguration declare their rows' foreign keys to Flock (#1087 S9).",
    "Cluckwork.Application.Modules.GeneralInventory.Inventory.RecordFeedUsage.RecordFeedUsageHandler",
    "Cluckwork.Application.Modules.GeneralInventory.Inventory.RecordWaterUsage.RecordWaterUsageHandler",
    "Cluckwork.Application.Modules.GeneralInventory.Inventory.UpdateWaterUsage.UpdateWaterUsageHandler",
    "Cluckwork.Infrastructure.Modules.GeneralInventory.Configurations.FeedUsageConfiguration",
    "Cluckwork.Infrastructure.Modules.GeneralInventory.Configurations.InventoryMovementConfiguration",
    "Cluckwork.Infrastructure.Modules.GeneralInventory.Configurations.WaterUsageConfiguration")]
internal static class GeneralInventoryModuleRules { }
