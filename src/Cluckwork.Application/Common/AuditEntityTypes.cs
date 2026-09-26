namespace Cluckwork.Application.Common;

public static class AuditEntityTypes
{
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        "Account", "Customer", "DailyEntry", "EggGrade", "EggLot", "EggUnitConversion",
        "Expense", "ExpenseCategory", "FarmLogo", "Flock", "InventoryItem", "Payment",
        "Product", "SalesOrder", "User", "WaterUsage"
    };
}
