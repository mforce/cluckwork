namespace Cluckwork.Domain.Common.Architecture;

[ModuleOwner("Insights", "module",
    Namespaces = [
        "Cluckwork.Application.Modules.Insights",
        "Cluckwork.Infrastructure.Modules.Insights",
    ],
    ReadModel = true)]
[ModuleEdge(
    "Insights", "Commerce", "R",
    "ReportQueries reads confirmed SalesOrders and Payments for sales and profit totals; ExportQueries streams Customers, SalesOrders, SalesOrderItems, SalesOrderAllocations and Payments. Reads compose in C# and never mutate Commerce rows.",
    "Cluckwork.Infrastructure.Modules.Insights.Repositories.ExportQueries",
    "Cluckwork.Infrastructure.Modules.Insights.Repositories.ReportQueries")]
[ModuleEdge(
    "Insights", "EggOperations", "R",
    "ReportQueries reads DailyEntries, DailyEntryGrades and EggGrades for production and grading totals; ExportQueries streams those records, EggLots and EggInventoryMovements with their existing filters and ordering.",
    "Cluckwork.Infrastructure.Modules.Insights.Repositories.ExportQueries",
    "Cluckwork.Infrastructure.Modules.Insights.Repositories.ReportQueries")]
[ModuleEdge(
    "Insights", "Farm", "R",
    "ReportQueries.AccountCurrencyAsync reads the current Account currency for report DTOs. The account query remains tenant-filtered and AsNoTracking.",
    "Cluckwork.Infrastructure.Modules.Insights.Repositories.ReportQueries")]
[ModuleEdge(
    "Insights", "Finance", "R",
    "ReportQueries aggregates Expenses for expense and profit totals; ExportQueries streams ExpenseCategories and Expenses as typed CSV rows. Neither query writes Finance data.",
    "Cluckwork.Infrastructure.Modules.Insights.Repositories.ExportQueries",
    "Cluckwork.Infrastructure.Modules.Insights.Repositories.ReportQueries")]
[ModuleEdge(
    "Insights", "FlockManagement", "R",
    "ExportQueries streams Flock and BirdMovement records; ReportQueries reads flock lifecycle and bird movements to calculate hen-day exposure. These are read-only owner-table queries composed in C#.",
    "Cluckwork.Infrastructure.Modules.Insights.Repositories.ExportQueries",
    "Cluckwork.Infrastructure.Modules.Insights.Repositories.ReportQueries")]
[ModuleEdge(
    "Insights", "GeneralInventory", "R",
    "ExportQueries streams InventoryItems, InventoryLots, InventoryMovements and FeedUsages as typed CSV rows. No inventory mutation or cross-owner SQL join is introduced.",
    "Cluckwork.Infrastructure.Modules.Insights.Repositories.ExportQueries")]
internal static class InsightsModuleRules { }
