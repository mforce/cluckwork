namespace Cluckwork.Domain.Common.Architecture;

[ModuleOwner("Farm", "module",
    Namespaces = [
        "Cluckwork.Domain.Modules.Farm",
        "Cluckwork.Application.Modules.Farm",
        "Cluckwork.Infrastructure.Modules.Farm",
    ],
    Implementations = [
        "Cluckwork.Infrastructure.Modules.Farm.Repositories.AccountRepository",
        "Cluckwork.Infrastructure.Modules.Farm.Repositories.FarmFixture",
        "Cluckwork.Infrastructure.Modules.Farm.Repositories.FarmLogoRepository",
    ],
    Seam = [
        "Cluckwork.Application.Modules.Farm.Accounts.IAccountRepository",
        "Cluckwork.Domain.Modules.Farm.Accounts.Account",
        "Cluckwork.Domain.Modules.Farm.Accounts.UserRoleAssignment",
    ])]
[ModuleEdge(
    "Farm", "Commerce", "R",
    "Farm settings own three Commerce-shaped values. Domain.Accounts.Account holds Domain.Catalog.EggUnit as the farm's default stepper unit and exposes Domain.Sales.DiscountCeiling, while UpdateFarmSettingsHandler resolves the stepper unit through Commerce's IEggUnitConversionLookup port (#854) and UpdateFarmSettingsValidator parses the ceiling with DiscountCeiling.TryParsePercent (#727). The Farm contract's FarmSettingsDetails, declared beside IFarmModule, carries the same Domain.Catalog.EggUnit stepper unit out to its callers (#851), and FarmModule fills its discount ceiling from DiscountCeiling.Percent. Design 3.4 shows Farm -> Commerce as none; this is live coupling the target design has still to remove.",
    "Cluckwork.Application.Modules.Farm.Accounts.FarmModule",
    "Cluckwork.Application.Modules.Farm.Contracts.FarmSettingsDetails",
    "Cluckwork.Application.Modules.Farm.Accounts.UpdateFarmSettings.UpdateFarmSettingsHandler",
    "Cluckwork.Application.Modules.Farm.Accounts.UpdateFarmSettings.UpdateFarmSettingsValidator",
    "Cluckwork.Domain.Modules.Farm.Accounts.Account")]
internal static class FarmModuleRules { }
