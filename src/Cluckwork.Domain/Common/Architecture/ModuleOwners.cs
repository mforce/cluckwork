using Cluckwork.Domain.Common.Architecture;

[assembly: ModuleOwner("Access", "module",
    Namespaces = [
        "Cluckwork.Application.Features.Users",
        "Cluckwork.Infrastructure.Identity",
    ],
    Implementations = [
        "Cluckwork.Infrastructure.Repositories.UserRoleAssignmentRepository",
    ],
    Types = [
        "Cluckwork.Application.Common.IIdentityProvider",
        "Cluckwork.Application.Common.IStepUpGrantService",
    ])]
[assembly: ModuleOwner("Farm", "module",
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
[assembly: ModuleOwner("FlockManagement", "module",
    Namespaces = [
        "Cluckwork.Domain.Flocks",
        "Cluckwork.Application.Features.Flocks",
    ],
    Implementations = [
        "Cluckwork.Infrastructure.Repositories.BirdMovementRepository",
        "Cluckwork.Infrastructure.Repositories.FlockFixture",
        "Cluckwork.Infrastructure.Repositories.FlockRepository",
    ])]
[assembly: ModuleOwner("EggOperations", "module",
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
[assembly: ModuleOwner("Commerce", "module",
    Namespaces = [
        "Cluckwork.Domain.Catalog",
        "Cluckwork.Domain.Sales",
        "Cluckwork.Application.Features.Catalog",
        "Cluckwork.Application.Features.Customers",
        "Cluckwork.Application.Features.Sales",
    ],
    Implementations = [
        "Cluckwork.Infrastructure.Repositories.CustomerRepository",
        "Cluckwork.Infrastructure.Repositories.CommerceFixture",
        "Cluckwork.Infrastructure.Repositories.EggUnitConversionRepository",
        "Cluckwork.Infrastructure.Repositories.PaymentRepository",
        "Cluckwork.Infrastructure.Repositories.ProductRepository",
        "Cluckwork.Infrastructure.Repositories.SalesOrderAllocationRepository",
        "Cluckwork.Infrastructure.Repositories.SalesOrderRepository",
    ])]
[assembly: ModuleOwner("GeneralInventory", "module",
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
[assembly: ModuleOwner("Finance", "module",
    Namespaces = [
        "Cluckwork.Domain.Expenses",
        "Cluckwork.Application.Features.Expenses",
    ],
    Implementations = [
        "Cluckwork.Infrastructure.Repositories.ExpenseCategoryRepository",
        "Cluckwork.Infrastructure.Repositories.ExpenseRepository",
        "Cluckwork.Infrastructure.Repositories.FinanceFixture",
    ])]
[assembly: ModuleOwner("Insights", "module",
    Namespaces = [
        "Cluckwork.Application.Features.Audit",
        "Cluckwork.Application.Features.Reports",
        "Cluckwork.Application.Features.Export",
        "Cluckwork.Application.Features.Insights",
        "Cluckwork.Infrastructure.Insights",
    ])]
[assembly: ModuleOwner("Platform", "platform",
    Namespaces = [
        "Cluckwork.Domain.Common",
        "Cluckwork.Domain.Auditing",
        "Cluckwork.Application.Common",
        "Cluckwork.Infrastructure",
        "Cluckwork.Api",
        "Cluckwork.AppHost",
        "Cluckwork.Analyzers",
    ],
    ExactNamespaces = [
        "Cluckwork.Domain",
        "Cluckwork.Application",
    ])]
