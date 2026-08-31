namespace Cluckwork.Api.Hosting;

using Cluckwork.Api.Configuration;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Application.Features.Customers;
using Cluckwork.Application.Features.Customers.CreateCustomer;
using Cluckwork.Application.Features.Customers.UpdateCustomer;
using Cluckwork.Application.Features.DailyEntries;
using Cluckwork.Application.Features.DailyEntries.AdjustDailyEntry;
using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;
using Cluckwork.Application.Features.DailyEntries.SubmitDailyEntry;
using Cluckwork.Application.Features.DailyEntries.VoidDailyEntry;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.EggGrades.CreateEggGrade;
using Cluckwork.Application.Features.EggGrades.SetEggGradeActive;
using Cluckwork.Application.Features.EggGrades.UpdateEggGrade;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Application.Features.EggLots.RecordEggLotMovement;
using Cluckwork.Application.Features.Expenses;
using Cluckwork.Application.Features.Expenses.AdjustExpense;
using Cluckwork.Application.Features.Expenses.CreateExpense;
using Cluckwork.Application.Features.Expenses.CreateExpenseCategory;
using Cluckwork.Application.Features.Expenses.UpdateExpenseCategory;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Application.Features.Flocks.ArchiveFlock;
using Cluckwork.Application.Features.Flocks.CreateFlock;
using Cluckwork.Application.Features.Flocks.DepleteFlock;
using Cluckwork.Application.Features.Flocks.ReactivateFlock;
using Cluckwork.Application.Features.Flocks.RecordBirdMovement;
using Cluckwork.Application.Features.Flocks.UpdateFlock;
using Cluckwork.Application.Features.Inventory;
using Cluckwork.Application.Features.Inventory.CreateInventoryItem;
using Cluckwork.Application.Features.Inventory.RecordAdjustment;
using Cluckwork.Application.Features.Inventory.RecordFeedUsage;
using Cluckwork.Application.Features.Inventory.RecordPurchase;
using Cluckwork.Application.Features.Inventory.RecordWaterUsage;
using Cluckwork.Application.Features.Inventory.UpdateInventoryItem;
using Cluckwork.Application.Features.Inventory.UpdateWaterUsage;
using Cluckwork.Application.Features.Sales;
using Cluckwork.Application.Features.Sales.AddOrderItem;
using Cluckwork.Application.Features.Sales.CancelSalesOrder;
using Cluckwork.Application.Features.Sales.ConfirmSale;
using Cluckwork.Application.Features.Sales.CreateSalesOrder;
using Cluckwork.Application.Features.Sales.RecordPayment;
using Cluckwork.Application.Features.Sales.RemoveOrderItem;
using Cluckwork.Application.Features.Sales.UpdateOrderItem;
using Cluckwork.Application.Features.Sales.VoidPayment;
using Cluckwork.Application.Features.Sales.VoidSale;
using Cluckwork.Application.Features.Users.CreateUser;
using Cluckwork.Application.Features.Users.SetLanguage;
using Cluckwork.Application.Features.Users.SetStepperUnit;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Providers;
using Cluckwork.Infrastructure.Repositories;
using Cluckwork.Infrastructure.Time;
using FluentValidation;
using Microsoft.Extensions.Options;

internal static class CluckworkFeatureServiceCollectionExtensions
{
    public static IServiceCollection AddCluckworkFeatures(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        AddPortsAndRepositories(services);
        AddValidators(services);
        AddHandlers(services);

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

    private static void AddPortsAndRepositories(IServiceCollection services)
    {
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<
            Cluckwork.Application.Features.Audit.IAuditEventRepository,
            AuditEventRepository>();
        services.AddScoped<
            Cluckwork.Application.Features.Eggs.IEggInventoryMovementRepository,
            EggInventoryMovementRepository>();
        services.AddScoped<
            Cluckwork.Application.Features.Users.IUserRoleAssignmentRepository,
            UserRoleAssignmentRepository>();
        services.AddScoped<IFlockScopeGuard, FlockScopeGuard>();
        services.AddScoped<
            Cluckwork.Application.Features.Export.IExportQueries,
            ExportQueries>();

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IClock, SystemClock>();
        services.AddScoped<IFarmClock, FarmClock>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IDailyEntryRepository, DailyEntryRepository>();
        services.AddScoped<IEggLotRepository, EggLotRepository>();
        services.AddScoped<IEggGradeRepository, EggGradeRepository>();
        services.AddScoped<
            Cluckwork.Application.Features.Catalog.IProductRepository,
            ProductRepository>();
        services.AddScoped<
            Cluckwork.Application.Features.Catalog.IEggUnitConversionRepository,
            EggUnitConversionRepository>();
        services.AddScoped<IExpenseCategoryRepository, ExpenseCategoryRepository>();
        services.AddScoped<IExpenseRepository, ExpenseRepository>();
        services.AddScoped<ISalesOrderRepository, SalesOrderRepository>();
        services.AddScoped<
            ISalesOrderAllocationRepository,
            SalesOrderAllocationRepository>();
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<
            Cluckwork.Application.Features.Reports.IReportQueries,
            ReportQueries>();
        services.AddScoped<IInventoryItemRepository, InventoryItemRepository>();
        services.AddScoped<IInventoryLotRepository, InventoryLotRepository>();
        services.AddScoped<
            IInventoryMovementRepository,
            InventoryMovementRepository>();
        services.AddScoped<IFeedUsageRepository, FeedUsageRepository>();
        services.AddScoped<IWaterUsageRepository, WaterUsageRepository>();
        services.AddScoped<IFlockRepository, FlockRepository>();
        services.AddScoped<IBirdMovementRepository, BirdMovementRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<
            Cluckwork.Application.Features.Accounts.ICurrencyBoundRowProbe,
            CurrencyBoundRowProbe>();
        services.AddScoped<
            Cluckwork.Application.Features.Accounts.IFarmLogoRepository,
            FarmLogoRepository>();
    }

    private static void AddValidators(IServiceCollection services)
    {
        services.AddScoped<
            IValidator<RecordDailyEntryCommand>,
            RecordDailyEntryValidator>();
        services.AddScoped<
            IValidator<Cluckwork.Application.Features.Catalog.CreateProduct.CreateProductCommand>,
            Cluckwork.Application.Features.Catalog.CreateProduct.CreateProductValidator>();
        services.AddScoped<
            IValidator<Cluckwork.Application.Features.Catalog.UpdateProduct.UpdateProductCommand>,
            Cluckwork.Application.Features.Catalog.UpdateProduct.UpdateProductValidator>();
        services.AddScoped<
            IValidator<Cluckwork.Application.Features.Catalog.UpdateEggUnitConversion.UpdateEggUnitConversionCommand>,
            Cluckwork.Application.Features.Catalog.UpdateEggUnitConversion.UpdateEggUnitConversionValidator>();
        services.AddScoped<IValidator<CreateFlockCommand>, CreateFlockValidator>();
        services.AddScoped<
            IValidator<CreateCustomerCommand>,
            CreateCustomerValidator>();
        services.AddScoped<
            IValidator<UpdateCustomerCommand>,
            UpdateCustomerValidator>();
        services.AddScoped<
            IValidator<CreateSalesOrderCommand>,
            CreateSalesOrderValidator>();
        services.AddScoped<
            IValidator<AddOrderItemCommand>,
            AddOrderItemValidator>();
        services.AddScoped<
            IValidator<UpdateOrderItemCommand>,
            UpdateOrderItemValidator>();
        services.AddScoped<
            IValidator<CreateEggGradeCommand>,
            CreateEggGradeValidator>();
        services.AddScoped<
            IValidator<UpdateEggGradeCommand>,
            UpdateEggGradeValidator>();
        services.AddScoped<
            IValidator<CreateExpenseCategoryCommand>,
            CreateExpenseCategoryValidator>();
        services.AddScoped<
            IValidator<UpdateExpenseCategoryCommand>,
            UpdateExpenseCategoryValidator>();
        services.AddScoped<
            IValidator<CreateExpenseCommand>,
            CreateExpenseValidator>();
        services.AddScoped<
            IValidator<AdjustExpenseCommand>,
            AdjustExpenseValidator>();
        services.AddScoped<IValidator<UpdateFlockCommand>, UpdateFlockValidator>();
        services.AddScoped<
            IValidator<RecordBirdMovementCommand>,
            RecordBirdMovementValidator>();
        services.AddScoped<IValidator<VoidSaleCommand>, VoidSaleValidator>();
        services.AddScoped<
            IValidator<RecordPaymentCommand>,
            RecordPaymentValidator>();
        services.AddScoped<
            IValidator<VoidPaymentCommand>,
            VoidPaymentValidator>();
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
            IValidator<RecordEggLotMovementCommand>,
            RecordEggLotMovementValidator>();
        services.AddScoped<
            IValidator<RecordWaterUsageCommand>,
            RecordWaterUsageValidator>();
        services.AddScoped<
            IValidator<UpdateWaterUsageCommand>,
            UpdateWaterUsageValidator>();
        services.AddScoped<IValidator<CreateUserCommand>, CreateUserValidator>();
        services.AddScoped<
            IValidator<Cluckwork.Application.Features.Users.UpdateUser.UpdateUserCommand>,
            Cluckwork.Application.Features.Users.UpdateUser.UpdateUserValidator>();
        services.AddScoped<
            IValidator<Cluckwork.Application.Features.Users.SetUserPassword.SetUserPasswordCommand>,
            Cluckwork.Application.Features.Users.SetUserPassword.SetUserPasswordValidator>();
        services.AddScoped<
            IValidator<Cluckwork.Application.Features.Users.ChangeOwnPassword.ChangeOwnPasswordCommand>,
            Cluckwork.Application.Features.Users.ChangeOwnPassword.ChangeOwnPasswordValidator>();
        services.AddScoped<
            IValidator<Cluckwork.Application.Features.Users.ChangeUserRole.ChangeUserRoleCommand>,
            Cluckwork.Application.Features.Users.ChangeUserRole.ChangeUserRoleValidator>();
        services.AddScoped<
            IValidator<Cluckwork.Application.Features.Users.ChangeUserEmail.ChangeUserEmailCommand>,
            Cluckwork.Application.Features.Users.ChangeUserEmail.ChangeUserEmailValidator>();
        // #356 — disable carries an optional reason; enable carries no body at
        // all and therefore has no validator.
        services.AddScoped<
            IValidator<Cluckwork.Application.Features.Users.DisableUser.DisableUserCommand>,
            Cluckwork.Application.Features.Users.DisableUser.DisableUserValidator>();
        // #309 — the login DTO validator lives in the Api assembly (it validates
        // the Api LoginRequest). MAX-length only; see LoginRequestValidator.
        services.AddScoped<
            IValidator<Cluckwork.Api.Endpoints.Auth.LoginRequest>,
            Cluckwork.Api.Endpoints.Auth.LoginRequestValidator>();
        // #308
        services.AddScoped<
            IValidator<Cluckwork.Api.Endpoints.Auth.StepUpRequest>,
            Cluckwork.Api.Endpoints.Auth.StepUpRequestValidator>();
        services.AddScoped<
            IValidator<AdjustDailyEntryCommand>,
            AdjustDailyEntryValidator>();
        services.AddScoped<
            IValidator<VoidDailyEntryCommand>,
            VoidDailyEntryValidator>();
        services.AddScoped<IValidator<SetLanguageCommand>, SetLanguageValidator>();
        services.AddScoped<IValidator<SetStepperUnitCommand>, SetStepperUnitValidator>();
        services.AddScoped<
            IValidator<Cluckwork.Application.Features.Accounts.UpdateFarmSettings.UpdateFarmSettingsCommand>,
            Cluckwork.Application.Features.Accounts.UpdateFarmSettings.UpdateFarmSettingsValidator>();
    }

    private static void AddHandlers(IServiceCollection services)
    {
        services.AddScoped<RecordDailyEntryHandler>();
        services.AddScoped<SubmitDailyEntryHandler>();
        services.AddScoped<CreateCustomerHandler>();
        services.AddScoped<UpdateCustomerHandler>();
        services.AddScoped<CreateSalesOrderHandler>();
        services.AddScoped<AddOrderItemHandler>();
        services.AddScoped<CancelSalesOrderHandler>();
        services.AddScoped<RemoveOrderItemHandler>();
        services.AddScoped<UpdateOrderItemHandler>();
        services.AddScoped<ConfirmSaleHandler>();
        services.AddScoped<VoidSaleHandler>();
        services.AddScoped<RecordPaymentHandler>();
        services.AddScoped<VoidPaymentHandler>();
        services.AddScoped<CreateInventoryItemHandler>();
        services.AddScoped<UpdateInventoryItemHandler>();
        services.AddScoped<SetInventoryItemActiveHandler>();
        services.AddScoped<RecordPurchaseHandler>();
        services.AddScoped<RecordFeedUsageHandler>();
        services.AddScoped<RecordAdjustmentHandler>();
        services.AddScoped<RecordEggLotMovementHandler>();
        services.AddScoped<RecordWaterUsageHandler>();
        services.AddScoped<UpdateWaterUsageHandler>();
        services.AddScoped<CreateFlockHandler>();
        services.AddScoped<DepleteFlockHandler>();
        services.AddScoped<CreateEggGradeHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Catalog.CreateProduct.CreateProductHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Catalog.UpdateProduct.UpdateProductHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Catalog.SetProductActive.SetProductActiveHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Catalog.UpdateEggUnitConversion.UpdateEggUnitConversionHandler>();
        services.AddScoped<UpdateEggGradeHandler>();
        services.AddScoped<SetEggGradeActiveHandler>();
        services.AddScoped<CreateExpenseCategoryHandler>();
        services.AddScoped<UpdateExpenseCategoryHandler>();
        services.AddScoped<CreateExpenseHandler>();
        services.AddScoped<AdjustExpenseHandler>();
        services.AddScoped<UpdateFlockHandler>();
        services.AddScoped<ArchiveFlockHandler>();
        services.AddScoped<RecordBirdMovementHandler>();
        services.AddScoped<ReactivateFlockHandler>();
        services.AddScoped<CreateUserHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Users.UpdateUser.UpdateUserHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Users.SetUserPassword.SetUserPasswordHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Users.ChangeOwnPassword.ChangeOwnPasswordHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Users.ChangeUserRole.ChangeUserRoleHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Users.ChangeUserEmail.ChangeUserEmailHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Users.DisableUser.DisableUserHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Users.EnableUser.EnableUserHandler>();
        services.AddScoped<SetLanguageHandler>();
        services.AddScoped<SetStepperUnitHandler>();
        services.AddScoped<AdjustDailyEntryHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Accounts.UpdateFarmSettings.UpdateFarmSettingsHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Accounts.SetFarmLogo.SetFarmLogoHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Accounts.RemoveFarmLogo.RemoveFarmLogoHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Accounts.SetFarmBanner.SetFarmBannerHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Accounts.RemoveFarmBanner.RemoveFarmBannerHandler>();
        services.AddScoped<VoidDailyEntryHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Users.AssignFlock.AssignFlockHandler>();
        services.AddScoped<
            Cluckwork.Application.Features.Users.AssignFlock.UnassignFlockHandler>();
    }
}
