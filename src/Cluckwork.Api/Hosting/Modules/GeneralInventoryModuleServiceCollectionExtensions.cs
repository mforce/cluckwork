using Cluckwork.Application.Features.Inventory;
using Cluckwork.Application.Features.Inventory.CreateInventoryItem;
using Cluckwork.Application.Features.Inventory.RecordAdjustment;
using Cluckwork.Application.Features.Inventory.RecordFeedUsage;
using Cluckwork.Application.Features.Inventory.RecordPurchase;
using Cluckwork.Application.Features.Inventory.RecordWaterUsage;
using Cluckwork.Application.Features.Inventory.UpdateInventoryItem;
using Cluckwork.Application.Features.Inventory.UpdateWaterUsage;
using Cluckwork.Infrastructure.Repositories;
using FluentValidation;

namespace Cluckwork.Api.Hosting.Modules;

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
