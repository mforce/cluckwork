namespace Cluckwork.Domain.Common.Architecture;

[ModuleOwner("Farm", "module",
    Namespaces = [
        "Cluckwork.Domain.Accounts",
        "Cluckwork.Domain.Media",
        "Cluckwork.Application.Features.Accounts",
    ],
    Implementations = [
        "Cluckwork.Infrastructure.Repositories.AccountRepository",
        "Cluckwork.Infrastructure.Repositories.FarmFixture",
        "Cluckwork.Infrastructure.Repositories.FarmLogoRepository",
    ],
    Seam = [
        "Cluckwork.Application.Features.Accounts.IAccountRepository",
        "Cluckwork.Domain.Accounts.Account",
        "Cluckwork.Domain.Accounts.UserRoleAssignment",
    ])]
[ModuleEdge(
    "Farm", "Commerce", "R",
    "Farm settings own three Commerce-shaped values. Domain.Accounts.Account holds Domain.Catalog.EggUnit as the farm's default stepper unit and exposes Domain.Sales.DiscountCeiling, while UpdateFarmSettingsHandler resolves the stepper unit through Commerce's IEggUnitConversionLookup port (#854) and UpdateFarmSettingsValidator parses the ceiling with DiscountCeiling.TryParsePercent (#727). The Farm contract's FarmSettingsDetails, declared beside IFarmModule, carries the same Domain.Catalog.EggUnit stepper unit out to its callers (#851), and FarmModule fills its discount ceiling from DiscountCeiling.Percent. Design 3.4 shows Farm -> Commerce as none; this is live coupling the target design has still to remove.",
    "Cluckwork.Application.Features.Accounts.FarmModule",
    "Cluckwork.Application.Features.Accounts.FarmSettingsDetails",
    "Cluckwork.Application.Features.Accounts.UpdateFarmSettings.UpdateFarmSettingsHandler",
    "Cluckwork.Application.Features.Accounts.UpdateFarmSettings.UpdateFarmSettingsValidator",
    "Cluckwork.Domain.Accounts.Account")]
internal static class FarmModuleRules { }
