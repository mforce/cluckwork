using Cluckwork.Api.Modules.Commerce;
using Cluckwork.Api.Modules.EggOperations;
using Cluckwork.Api.Modules.Finance;
using Cluckwork.Api.Modules.FlockManagement;
using Cluckwork.Api.Modules.GeneralInventory;
using Cluckwork.Api.Modules.Insights;

namespace Cluckwork.Api.Hosting.Modules;

// #858 — one registration file per owner. Access registers through
// AddCluckworkIdentity, and each seeder fixture port through
// AddCluckworkPersistence's non-Production gate.
internal static class CluckworkModulesServiceCollectionExtensions
{
    public static IServiceCollection AddCluckworkModules(
        this IServiceCollection services, IConfiguration configuration) =>
        services
            .AddCluckworkPlatformServices(configuration)
            .AddFarmModule(configuration)
            .AddFlockManagementModule()
            .AddEggOperationsModule()
            .AddCommerceModule()
            .AddGeneralInventoryModule()
            .AddFinanceModule()
            .AddInsightsModule();
}
