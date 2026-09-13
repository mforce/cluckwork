using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations;

public partial class AddBusinessRecordChronology : Migration
{
    private const string UnknownCreatedAtUtc = "1970-01-01 00:00:00+00";

    private static readonly string[] NewCreatedAtTables =
    [
        "Accounts", "Flocks", "BirdMovements", "DailyEntries", "DailyEntryGrades",
        "EggGrades", "EggLots", "Customers", "SalesOrders", "SalesOrderItems",
        "SalesOrderAllocations", "Payments", "InventoryItems", "InventoryLots",
        "ExpenseCategories", "Expenses", "Products", "ProductEggGradeMappings",
        "EggUnitConversions", "UserRoleAssignments", "FarmLogos", "AspNetUsers"
    ];

    private static readonly (string Table, string EntityType, string[] Actions)[] CreatedAuditMappings =
    [
        ("Accounts", "Account", ["Account.Provisioned"]),
        ("Flocks", "Flock", ["Flock.Create"]),
        ("BirdMovements", "BirdMovement", []),
        ("DailyEntries", "DailyEntry", ["DailyEntry.Create"]),
        ("DailyEntryGrades", "DailyEntryGrade", []),
        ("EggGrades", "EggGrade", ["EggGrade.Create"]),
        ("EggLots", "EggLot", []),
        ("Customers", "Customer", ["Customer.Create"]),
        ("SalesOrders", "SalesOrder", ["SalesOrder.Create"]),
        ("SalesOrderItems", "SalesOrderItem", []),
        ("SalesOrderAllocations", "SalesOrderAllocation", []),
        ("Payments", "Payment", []),
        ("InventoryItems", "InventoryItem", []),
        ("InventoryLots", "InventoryLot", []),
        ("ExpenseCategories", "ExpenseCategory", []),
        ("Expenses", "Expense", ["Expense.Create"]),
        ("Products", "Product", ["Product.Create"]),
        ("ProductEggGradeMappings", "ProductEggGradeMapping", []),
        ("EggUnitConversions", "EggUnitConversion", []),
        ("UserRoleAssignments", "UserRoleAssignment", []),
        ("FarmLogos", "FarmLogo", ["Account.SetLogo", "Account.SetBanner"]),
        ("AspNetUsers", "User", ["User.Create"])
    ];

    private static readonly (string Table, string EntityType, string[] Actions)[] MutableAuditMappings =
    [
        ("Accounts", "Account",
            ["Account.UpdateSettings", "Account.Suspend", "Account.Reactivate", "Account.Rename"]),
        ("Flocks", "Flock",
            ["Flock.Update", "Flock.Deplete", "Flock.Archive", "Flock.Reactivate"]),
        ("DailyEntries", "DailyEntry",
            ["DailyEntry.Update", "DailyEntry.Submit", "DailyEntry.Adjust", "DailyEntry.Void"]),
        ("DailyEntryGrades", "DailyEntryGrade", []),
        ("EggGrades", "EggGrade",
            ["EggGrade.Update", "EggGrade.Activate", "EggGrade.Deactivate"]),
        ("EggLots", "EggLot", ["EggLot.Movement"]),
        ("Customers", "Customer", ["Customer.Update"]),
        ("SalesOrders", "SalesOrder",
            ["SalesOrder.AddItem", "SalesOrder.UpdateItem", "SalesOrder.RemoveItem",
             "SalesOrder.Confirm", "SalesOrder.Cancel", "SalesOrder.Void"]),
        ("SalesOrderItems", "SalesOrderItem", []),
        ("SalesOrderAllocations", "SalesOrderAllocation", []),
        ("Payments", "Payment", ["Payment.Void"]),
        ("InventoryItems", "InventoryItem", []),
        ("InventoryLots", "InventoryLot", []),
        ("WaterUsages", "WaterUsage", ["WaterUsage.Correct"]),
        ("ExpenseCategories", "ExpenseCategory", ["ExpenseCategory.Update"]),
        ("Expenses", "Expense", ["Expense.Adjust"]),
        ("Products", "Product", ["Product.Update", "Product.Activate", "Product.Deactivate"]),
        ("ProductEggGradeMappings", "ProductEggGradeMapping", []),
        ("EggUnitConversions", "EggUnitConversion", ["EggUnitConversion.Update"]),
        ("FarmLogos", "FarmLogo",
            ["Account.SetLogo", "Account.RemoveLogo", "Account.SetBanner", "Account.RemoveBanner"]),
        ("AspNetUsers", "User",
            ["User.Update", "User.PasswordSet", "User.PasswordChanged", "User.EmailChanged",
             "User.Disabled", "User.Enabled", "User.BreakGlassReset"])
    ];

    private static readonly (string Table, string OrderBy)[] ChronologicalTables =
    [
        ("SalesOrders", "\"OrderDate\", \"CreatedAtUtc\", \"Id\""),
        ("Expenses", "\"Date\", \"CreatedAtUtc\", \"Id\""),
        ("DailyEntries", "\"Date\", \"CreatedAtUtc\", \"Id\""),
        ("EggLots", "\"ProductionDate\", \"CreatedAtUtc\", \"Id\""),
        ("BirdMovements", "\"Date\", \"CreatedAtUtc\", \"Id\""),
        ("Payments", "\"PaymentDate\", \"CreatedAtUtc\", \"Id\""),
        ("InventoryLots", "\"ReceivedDate\", \"CreatedAtUtc\", \"Id\""),
        ("FeedUsages", "\"Date\", \"CreatedAtUtc\", \"Id\""),
        ("WaterUsages", "\"Date\", \"CreatedAtUtc\", \"Id\""),
        ("InventoryMovements", "\"Date\", \"CreatedAtUtc\", \"Id\""),
        ("EggInventoryMovements", "\"CreatedAtUtc\", \"Id\"")
    ];

    private static readonly string[] CreatedOnlyTables =
    [
        "BirdMovements", "FeedUsages", "InventoryMovements",
        "EggInventoryMovements", "UserRoleAssignments"
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

        foreach (var table in NewCreatedAtTables)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAtUtc",
                table: table,
                type: "timestamp with time zone",
                nullable: true);
        }

        foreach (var (table, _, _) in MutableAuditMappings)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAtUtc",
                table: table,
                type: "timestamp with time zone",
                nullable: true);
        }

        foreach (var mapping in CreatedAuditMappings)
            BackfillCreatedAtUtc(migrationBuilder, mapping.Table, mapping.EntityType, mapping.Actions);

        foreach (var mapping in MutableAuditMappings)
            BackfillUpdatedAtUtc(migrationBuilder, mapping.Table, mapping.EntityType, mapping.Actions);

        foreach (var table in NewCreatedAtTables)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "CreatedAtUtc",
                table: table,
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }

        foreach (var (table, _, _) in MutableAuditMappings)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "UpdatedAtUtc",
                table: table,
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }

        foreach (var (table, orderBy) in ChronologicalTables)
        {
            AddSequence(migrationBuilder, table, orderBy);
            migrationBuilder.CreateIndex(
                name: $"IX_{table}_Sequence",
                table: table,
                column: "Sequence",
                unique: true);
        }

        migrationBuilder.Sql(
            """
            CREATE FUNCTION "StampMutableBusinessRecord"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            DECLARE
                stamp timestamp with time zone := clock_timestamp();
            BEGIN
                IF TG_OP = 'INSERT' THEN
                    NEW."CreatedAtUtc" := stamp;
                    NEW."UpdatedAtUtc" := stamp;
                ELSE
                    NEW."CreatedAtUtc" := OLD."CreatedAtUtc";
                    NEW."UpdatedAtUtc" := stamp;
                END IF;
                RETURN NEW;
            END;
            $$;

            CREATE FUNCTION "StampCreatedBusinessRecord"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF TG_OP = 'INSERT' THEN
                    NEW."CreatedAtUtc" := clock_timestamp();
                ELSE
                    NEW."CreatedAtUtc" := OLD."CreatedAtUtc";
                END IF;
                RETURN NEW;
            END;
            $$;
            """);

        foreach (var (table, _, _) in MutableAuditMappings)
            AddTrigger(migrationBuilder, table, "StampMutableBusinessRecord");
        foreach (var table in CreatedOnlyTables)
            AddTrigger(migrationBuilder, table, "StampCreatedBusinessRecord");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var (table, _, _) in MutableAuditMappings)
            DropTrigger(migrationBuilder, table);
        foreach (var table in CreatedOnlyTables)
            DropTrigger(migrationBuilder, table);

        migrationBuilder.Sql(
            """
            DROP FUNCTION "StampMutableBusinessRecord"();
            DROP FUNCTION "StampCreatedBusinessRecord"();
            """);

        foreach (var (table, _) in ChronologicalTables)
        {
            migrationBuilder.DropIndex(name: $"IX_{table}_Sequence", table: table);
            migrationBuilder.DropColumn(name: "Sequence", table: table);
        }

        foreach (var (table, _, _) in MutableAuditMappings)
            migrationBuilder.DropColumn(name: "UpdatedAtUtc", table: table);

        foreach (var table in NewCreatedAtTables)
            migrationBuilder.DropColumn(name: "CreatedAtUtc", table: table);
    }

    private static void BackfillCreatedAtUtc(
        MigrationBuilder migrationBuilder,
        string table,
        string entityType,
        string[] actions)
    {
        var auditTime = actions.Length == 0
            ? $"TIMESTAMPTZ '{UnknownCreatedAtUtc}'"
            : $"""
               COALESCE((
                   SELECT MIN(a."OccurredAtUtc")
                   FROM "AuditEvents" AS a
                   WHERE a."EntityType" = '{entityType}'
                     AND a."EntityId" = row."Id"
                     AND a."Action" IN ({SqlLiterals(actions)})
               ), TIMESTAMPTZ '{UnknownCreatedAtUtc}')
               """;

        migrationBuilder.Sql($"""
            UPDATE "{table}" AS row
            SET "CreatedAtUtc" = {auditTime};
            """);
    }

    private static void BackfillUpdatedAtUtc(
        MigrationBuilder migrationBuilder,
        string table,
        string entityType,
        string[] actions)
    {
        var updateTime = actions.Length == 0
            ? """row."CreatedAtUtc" """
            : $"""
               GREATEST(
                   row."CreatedAtUtc",
                   COALESCE((
                       SELECT MAX(a."OccurredAtUtc")
                       FROM "AuditEvents" AS a
                       WHERE a."EntityType" = '{entityType}'
                         AND a."EntityId" = row."Id"
                         AND a."Action" IN ({SqlLiterals(actions)})
                   ), row."CreatedAtUtc"))
               """;

        migrationBuilder.Sql($"""
            UPDATE "{table}" AS row
            SET "UpdatedAtUtc" = {updateTime};
            """);
    }

    private static void AddSequence(MigrationBuilder migrationBuilder, string table, string orderBy)
    {
        migrationBuilder.Sql($"""
            ALTER TABLE "{table}" ADD COLUMN "Sequence" bigint;

            WITH ranked AS (
                SELECT "Id", row_number() OVER (ORDER BY {orderBy}) AS sequence
                FROM "{table}"
            )
            UPDATE "{table}" AS target
            SET "Sequence" = ranked.sequence
            FROM ranked
            WHERE target."Id" = ranked."Id";

            ALTER TABLE "{table}" ALTER COLUMN "Sequence" SET NOT NULL;
            ALTER TABLE "{table}" ALTER COLUMN "Sequence" ADD GENERATED ALWAYS AS IDENTITY;

            SELECT setval(
                pg_get_serial_sequence('"{table}"', 'Sequence'),
                COALESCE((SELECT MAX("Sequence") FROM "{table}"), 0) + 1,
                false);
            """);
    }

    private static void AddTrigger(MigrationBuilder migrationBuilder, string table, string function)
    {
        migrationBuilder.Sql($"""
            CREATE TRIGGER "TR_{table}_BusinessRecordTimestamps"
            BEFORE INSERT OR UPDATE ON "{table}"
            FOR EACH ROW EXECUTE FUNCTION "{function}"();
            """);
    }

    private static void DropTrigger(MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($"""
            DROP TRIGGER "TR_{table}_BusinessRecordTimestamps" ON "{table}";
            """);
    }

    private static string SqlLiterals(IEnumerable<string> values) =>
        string.Join(", ", values.Select(value => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'"));
}
