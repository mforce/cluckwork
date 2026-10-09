using Cluckwork.Api.Configuration;
using Cluckwork.Application.Modules.Farm.Accounts;
using Cluckwork.Application.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Modules.Commerce.Repositories;
using Cluckwork.Infrastructure.Modules.Farm.Repositories;
using Cluckwork.Infrastructure.Modules.Finance.Repositories;
using Cluckwork.Infrastructure.Modules.GeneralInventory.Repositories;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace Cluckwork.Api.Modules.Farm;

internal static class FarmModuleServiceCollectionExtensions
{
    public static IServiceCollection AddFarmModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IFarmDirectory, AccountRepository>();
        services.AddScoped<
            ICurrencyBoundRowProbe,
            CurrencyBoundRowProbe>();
        // #854: the probe asks these in this order and stops at the first yes;
        // the three spec §4.6 names come first. Payments and FeedUsages cannot
        // be tested alone: a payment exists only against a sales order and a
        // feed usage only against the lot it drew from, so the source before
        // each always answers first. They stay because the rule is that a row
        // carrying an amount locks the currency, and these carry one.
        services.AddScoped<ICurrencyBoundRowSource, SalesOrderRepository>();
        services.AddScoped<ICurrencyBoundRowSource, PaymentRepository>();
        services.AddScoped<ICurrencyBoundRowSource, ExpenseRepository>();
        services.AddScoped<ICurrencyBoundRowSource, ProductRepository>();
        services.AddScoped<ICurrencyBoundRowSource, InventoryLotRepository>();
        services.AddScoped<ICurrencyBoundRowSource, FeedUsageRepository>();
        services.AddScoped<ICurrencyBoundRowSource, InventoryItemRepository>();
        services.AddScoped<
            IFarmLogoRepository,
            FarmLogoRepository>();
        services.AddScoped<
            IValidator<Cluckwork.Application.Modules.Farm.Contracts.UpdateFarmSettingsCommand>,
            Cluckwork.Application.Modules.Farm.Accounts.UpdateFarmSettings.UpdateFarmSettingsValidator>();
        services.AddScoped<
            Cluckwork.Application.Modules.Farm.Accounts.UpdateFarmSettings.UpdateFarmSettingsHandler>();
        services.AddScoped<
            IValidator<Cluckwork.Application.Modules.Farm.Contracts.SetConnectedAppsCommand>,
            Cluckwork.Application.Modules.Farm.Accounts.SetConnectedApps.SetConnectedAppsValidator>();
        services.AddScoped<
            Cluckwork.Application.Modules.Farm.Accounts.SetConnectedApps.SetConnectedAppsHandler>();
        services.AddScoped<
            Cluckwork.Application.Modules.Farm.Accounts.SetFarmLogo.SetFarmLogoHandler>();
        services.AddScoped<
            Cluckwork.Application.Modules.Farm.Accounts.RemoveFarmLogo.RemoveFarmLogoHandler>();
        services.AddScoped<
            Cluckwork.Application.Modules.Farm.Accounts.SetFarmBanner.SetFarmBannerHandler>();
        services.AddScoped<
            Cluckwork.Application.Modules.Farm.Accounts.RemoveFarmBanner.RemoveFarmBannerHandler>();
        services.AddScoped<
            IFarmModule,
            FarmModule>();

        services.AddOptions<FarmLogoOptions>()
            .Bind(configuration.GetSection(FarmLogoOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<FarmLogoOptions>,
            FarmLogoOptionsValidator>();

        services.AddOptions<FarmBannerOptions>()
            .Bind(configuration.GetSection(FarmBannerOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<FarmBannerOptions>,
            FarmBannerOptionsValidator>();

        return services;
    }
}
