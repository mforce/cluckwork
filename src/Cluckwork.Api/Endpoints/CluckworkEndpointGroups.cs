using Cluckwork.Api.Endpoints.ClientErrors;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Api.Modules.Access.Me;
using Cluckwork.Api.Modules.Access.OAuth;
using Cluckwork.Api.Modules.Access.Users;
using Cluckwork.Api.Modules.Commerce.Catalog;
using Cluckwork.Api.Modules.Commerce.Customers;
using Cluckwork.Api.Modules.Commerce.Sales;
using Cluckwork.Api.Modules.EggOperations.DailyEntries;
using Cluckwork.Api.Modules.EggOperations.EggGrades;
using Cluckwork.Api.Modules.EggOperations.Stock;
using Cluckwork.Api.Modules.Farm.Accounts;
using Cluckwork.Api.Modules.Finance.Expenses;
using Cluckwork.Api.Modules.FlockManagement.Flocks;
using Cluckwork.Api.Modules.GeneralInventory.Inventory;
using Cluckwork.Api.Modules.GeneralInventory.Water;
using Cluckwork.Api.Modules.Insights.Audit;
using Cluckwork.Api.Modules.Insights.Export;
using Cluckwork.Api.Modules.Insights.Reports;

namespace Cluckwork.Api.Endpoints;

// The /api/v1 endpoint groups. Program.cs calls this after the middleware pipeline and
// before the health, /error and SPA fallback routes.
internal static class CluckworkEndpointGroups
{
    public static void MapCluckworkEndpointGroups(this WebApplication app, CluckworkIdentityRegistration identity)
    {
        app.MapGroup("/api/v1/auth")
            .WithTags("Auth")
            // Rate-limit policies are applied per endpoint inside MapAuthEndpoints:
            // login and refresh differ, and authenticated /logout is not limited (#143).
            .MapAuthEndpoints();

        app.MapGroup("/api/v1/flocks")
            .WithTags("Flocks")
            .RequireAuthorization()
            .MapFlockEndpoints();

        app.MapGroup("/api/v1/egg-grades")
            .WithTags("EggGrades")
            .RequireAuthorization()
            .MapEggGradeEndpoints();

        // Product catalog + packed-unit conversions (#97): writes admin-gated inside.
        app.MapGroup("/api/v1/products")
            .WithTags("Catalog")
            .RequireAuthorization()
            .MapProductEndpoints();

        app.MapGroup("/api/v1/egg-unit-conversions")
            .WithTags("Catalog")
            .RequireAuthorization()
            .MapEggUnitConversionEndpoints();

        // Money data — admin end to end (#87), reads included.
        app.MapGroup("/api/v1/expense-categories")
            .WithTags("Expenses")
            .RequireAuthorization(AuthPolicies.AdminOnly)
            .MapExpenseCategoryEndpoints();

        app.MapGroup("/api/v1/expenses")
            .WithTags("Expenses")
            .RequireAuthorization(AuthPolicies.AdminOnly)
            .MapExpenseEndpoints();

        app.MapGroup("/api/v1/account")
            .WithTags("Account")
            .RequireAuthorization()
            .MapAccountEndpoints()
            .MapFarmLogoEndpoints()
            .MapFarmBannerEndpoints();

        // #45 — user-scoped sibling of /account: identity comes from the JWT, not the
        // farm. DEFAULT policy (not a named one) so every role, ReadOnly included, can
        // read their own identity and language preference.
        app.MapGroup("/api/v1/me")
            .WithTags("Me")
            .RequireAuthorization()
            .MapMeEndpoints();

        app.MapGroup("/api/v1/inventory")
            .WithTags("Inventory")
            .RequireAuthorization()
            .MapInventoryEndpoints();

        app.MapGroup("/api/v1/water-usage")
            .WithTags("WaterUsage")
            .RequireAuthorization()
            .MapWaterUsageEndpoints();

        app.MapGroup("/api/v1/daily-entries")
            .WithTags("DailyEntries")
            .RequireAuthorization()
            .MapDailyEntryEndpoints();

        app.MapGroup("/api/v1/stock")
            .WithTags("Stock")
            .RequireAuthorization()
            .MapStockEndpoints();

        app.MapGroup("/api/v1/customers")
            .WithTags("Customers")
            .RequireAuthorization()
            .MapCustomerEndpoints()
            .MapCustomerBalanceEndpoints();

        app.MapGroup("/api/v1/sales")
            .WithTags("Sales")
            .RequireAuthorization()
            .MapSaleEndpoints()
            .MapOrderPaymentEndpoints();

        // Payments are the Sales role's job (spec §5.1, #103): Owner/Manager/Sales.
        // The rest of the money tier (expenses, money reports, audit, export) stays
        // Owner/Manager.
        app.MapGroup("/api/v1/payments")
            .WithTags("Payments")
            .RequireAuthorization(AuthPolicies.SalesAccess)
            .MapPaymentEndpoints();

        // Reports (#91): production is open; the money routes carry their own
        // AdminOnly inside the group.
        app.MapGroup("/api/v1/reports")
            .WithTags("Reports")
            .RequireAuthorization()
            .MapReportEndpoints();

        // Audit trail (#93): read-only, admin-only.
        app.MapGroup("/api/v1/audit")
            .WithTags("Audit")
            .RequireAuthorization(AuthPolicies.AdminOnly)
            .MapAuditEndpoints();

        // User management is the OWNER's alone (#103) — Managers run the farm, the
        // Owner decides who works on it.
        app.MapGroup("/api/v1/users")
            .WithTags("Users")
            .RequireAuthorization(AuthPolicies.OwnerOnly)
            .MapUserEndpoints();

        // Manual backup (#95): CSV export, read-only, admin-only.
        app.MapGroup("/api/v1/export")
            .WithTags("Export")
            .RequireAuthorization(AuthPolicies.AdminOnly)
            .MapExportEndpoints();

        // #795 — OpenIddict answers the token endpoint itself; only authorization reaches an endpoint.
        // #806 — /mcp accepts only OAuth tokens, so it exists only beside the server that issues them.
        if (identity.OAuthIssuer is not null)
        {
            app.MapGroup("/api/v1/oauth")
                .WithTags("OAuth")
                .MapOAuthEndpoints();
            app.MapCluckworkMcp();
        }

        // #217 — browser error reports. Anonymous (the login screen can crash too);
        // the endpoint carries its own per-IP rate limit and size cap inside.
        app.MapGroup("/api/v1/client-errors")
            .WithTags("ClientErrors")
            .MapClientErrorEndpoints();
    }
}
