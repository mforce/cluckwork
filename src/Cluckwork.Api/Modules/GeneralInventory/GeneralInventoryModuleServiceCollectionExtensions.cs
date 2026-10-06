using Cluckwork.Application.Modules.GeneralInventory.Contracts;
using Cluckwork.Application.Modules.GeneralInventory.Inventory;
using Cluckwork.Application.Modules.GeneralInventory.Inventory.CreateInventoryItem;
using Cluckwork.Application.Modules.GeneralInventory.Inventory.RecordAdjustment;
using Cluckwork.Application.Modules.GeneralInventory.Inventory.RecordFeedUsage;
using Cluckwork.Application.Modules.GeneralInventory.Inventory.RecordPurchase;
using Cluckwork.Application.Modules.GeneralInventory.Inventory.RecordWaterUsage;
using Cluckwork.Application.Modules.GeneralInventory.Inventory.UpdateInventoryItem;
using Cluckwork.Application.Modules.GeneralInventory.Inventory.UpdateWaterUsage;
using Cluckwork.Infrastructure.Modules.GeneralInventory.Repositories;
using FluentValidation;

namespace Cluckwork.Api.Modules.GeneralInventory;

internal static class GeneralInventoryModuleServiceCollectionExtensions
{
    public static IServiceCollection AddGeneralInventoryModule(this IServiceCollection services)
    {
        services.AddScoped<IInventoryItemRepository, InventoryItemRepository>();
        services.AddScoped<IInventoryLotRepository, InventoryLotRepository>();
        services.AddScoped<
            IInventoryMovementRepository,
            InventoryMovementRepository>();
        services.AddScoped<IFeedUsageRepository, FeedUsageRepository>();
        services.AddScoped<IWaterUsageRepository, WaterUsageRepository>();
        services.AddScoped<
            IValidator<CreateInventoryItemCommand>,
            CreateInventoryItemValidator>();
        services.AddScoped<
            IValidator<UpdateInventoryItemCommand>,
            UpdateInventoryItemValidator>();
        services.AddScoped<
            IValidator<RecordPurchaseCommand>,
            RecordPurchaseValidator>();
        services.AddScoped<
            IValidator<RecordFeedUsageCommand>,
            RecordFeedUsageValidator>();
        services.AddScoped<
            IValidator<RecordAdjustmentCommand>,
            RecordAdjustmentValidator>();
        services.AddScoped<
            IValidator<RecordWaterUsageCommand>,
            RecordWaterUsageValidator>();
        services.AddScoped<
            IValidator<UpdateWaterUsageCommand>,
            UpdateWaterUsageValidator>();
        services.AddScoped<CreateInventoryItemHandler>();
        services.AddScoped<UpdateInventoryItemHandler>();
        services.AddScoped<SetInventoryItemActiveHandler>();
        services.AddScoped<RecordPurchaseHandler>();
        services.AddScoped<RecordFeedUsageHandler>();
        services.AddScoped<RecordAdjustmentHandler>();
        services.AddScoped<RecordWaterUsageHandler>();
        services.AddScoped<UpdateWaterUsageHandler>();
        services.AddScoped<IInventoryModule, InventoryModule>();

        return services;
    }
}
