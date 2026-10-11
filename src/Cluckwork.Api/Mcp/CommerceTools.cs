using System.ComponentModel;
using Cluckwork.Application.Modules.Commerce.Contracts;
using Cluckwork.Application.Modules.EggOperations.Contracts;
using Cluckwork.Application.Modules.Farm.Contracts;
using Cluckwork.Domain.Modules.Commerce.Contracts;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cluckwork.Api.Mcp;

// #808 — customers, sales orders, payments and balances. Amounts cross as McpMoney, never
// as minor units. Every farm-entered text field is left out (#792): a customer's phone,
// email, address and note, an order's void reason and discount note, and a payment's
// reference, note and void reason. Customer, grade names and the code-set order reference stay.
[McpServerToolType]
public sealed class CommerceTools
{
    private readonly McpCallContext call;
    private readonly ICommerceModule commerce;
    private readonly IEggGradeLookup grades;
    private readonly IFarmModule farm;
    private readonly IAuthorizationService authorization;

    public CommerceTools(
        McpCallContext call, ICommerceModule commerce, IEggGradeLookup grades, IFarmModule farm,
        IAuthorizationService authorization)
    {
        this.call = call;
        this.commerce = commerce;
        this.grades = grades;
        this.farm = farm;
        this.authorization = authorization;
    }

    [McpServerTool(Name = "cluckwork_list_customers", ReadOnly = true)]
    [Authorize(Policy = AuthPolicies.SalesFlow)]
    [Authorize(Policy = AuthPolicies.FarmReadScope)]
    [MirrorsRoute("ListCustomers")]
    [Description("The farm's customers by name, with the ids the order tools filter by. "
        + "Paged: when complete is false, call again with offset set to nextOffset.")]
    public async Task<McpPage<CustomerRow>> ListCustomersAsync(
        [Description("Only customers whose name contains this text, ignoring case.")] string? nameContains = null,
        [Description("Customers per page, 1 to 100.")] int limit = McpPage.DefaultLimit,
        int offset = 0,
        CancellationToken ct = default) =>
        await McpPage<CustomerRow>.ReadAsync(limit, offset,
            (take, skip, ct) => commerce.SearchCustomersAsync(nameContains, take, skip, ct),
            c => new CustomerRow(c.Id, c.Name),
            ct);

    // One tool for list and detail: every row carries its lines. What an order still owes
    // is the Sales tier's, asked through the live policy as GET /sales does (#769), so
    // widening this tool's gate never widens who sees money.
    [McpServerTool(Name = "cluckwork_list_sales_orders", ReadOnly = true)]
    [Authorize(Policy = AuthPolicies.SalesFlow)]
    [Authorize(Policy = AuthPolicies.FarmReadScope)]
    [MirrorsRoute("ListSalesOrders", "GetSalesOrder")]
    [Description("Sales orders, newest first, each with its lines and total. Confirmed orders carry what they still "
        + "owe when you may see payments; outstanding is null otherwise. "
        + "Paged: when complete is false, call again with offset set to nextOffset.")]
    public async Task<McpPage<SalesOrderRow>> ListSalesOrdersAsync(
        [Description("Draft, Confirmed, Shipped, Invoiced, Cancelled or Voided.")] string? status = null,
        [Description("Only this customer's orders, by id from cluckwork_list_customers.")] Guid? customerId = null,
        [Description("Earliest order date, yyyy-mm-dd.")] DateOnly? from = null,
        [Description("Latest order date, yyyy-mm-dd.")] DateOnly? to = null,
        [Description("Only orders that still owe money. Needs the Owner, Manager or Sales role.")] bool unpaid = false,
        [Description("Orders per page, 1 to 100.")] int limit = McpPage.DefaultLimit,
        int offset = 0,
        CancellationToken ct = default)
    {
        SalesOrderStatus? statusFilter = null;
        if (status is not null)
        {
            // IsDefined too: TryParse accepts any number.
            if (!Enum.TryParse<SalesOrderStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                throw new McpException($"Unknown status '{status}'. Use Draft, Confirmed, Shipped, Invoiced, Cancelled or Voided.");
            statusFilter = parsed;
        }

        var maySeeMoney = (await authorization.AuthorizeAsync(call.User, AuthPolicies.SalesAccess)).Succeeded;
        if (unpaid && !maySeeMoney)
            throw new McpException("Filtering orders by what they still owe needs the Owner, Manager or Sales role.");
        var settlement = !maySeeMoney ? SettlementScope.Hidden
            : unpaid ? SettlementScope.UnpaidOnly
            : SettlementScope.Visible;
        var filter = new SalesOrderListFilter(statusFilter, customerId, from, to, settlement);

        return await McpPage<SalesOrderRow>.ReadAsync(limit, offset, async (take, skip, ct) =>
            {
                var rows = await commerce.ListSalesOrdersAsync(filter, take, skip, ct);
                var customers = await commerce.GetCustomerNamesAsync([.. rows.Select(r => r.Order.CustomerId).Distinct()], ct);
                var gradeNames = await grades.GetDisplayNamesAsync(
                    [.. rows.SelectMany(r => r.Order.Items).Select(i => i.EggGradeId).Distinct()], ct);
                return (IReadOnlyList<SalesOrderRow>)[.. rows.Select(r => ToRow(r, customers, gradeNames))];
            },
            r => r,
            ct);
    }

    // ponytail: the contract loads one order's payments whole and this pages them in memory.
    // An order refuses overpayment, so its rows are bounded by the payments made against it
    // plus the ones an Owner or Manager voided. Page it in the query if orders ever carry hundreds.
    [McpServerTool(Name = "cluckwork_list_order_payments", ReadOnly = true)]
    [Authorize(Policy = AuthPolicies.SalesAccess)]
    [Authorize(Policy = AuthPolicies.FarmReadScope)]
    [MirrorsRoute("ListOrderPayments")]
    [Description("One sales order's payments, oldest first, voided ones included and marked. "
        + "Paged: when complete is false, call again with offset set to nextOffset.")]
    public async Task<McpPage<PaymentRow>> ListOrderPaymentsAsync(
        [Description("The order's id, from cluckwork_list_sales_orders.")] Guid orderId,
        [Description("Payments per page, 1 to 100.")] int limit = McpPage.DefaultLimit,
        int offset = 0,
        CancellationToken ct = default)
    {
        if (await commerce.GetSalesOrderAsync(orderId, ct) is null)
            throw new McpException($"No sales order {orderId} exists on this farm.");

        return await McpPage<PaymentRow>.ReadAsync(limit, offset,
            async (take, skip, ct) => (IReadOnlyList<PaymentDetails>)[.. (await commerce.ListOrderPaymentsAsync(orderId, ct)).Skip(skip).Take(take)],
            p => new PaymentRow(
                p.Id, p.PaymentDate, McpMoney.From(p.AmountMinorUnits, p.CurrencyCode, p.CurrencyMinorUnit),
                p.Method.ToString(), p.Voided),
            ct);
    }

    [McpServerTool(Name = "cluckwork_list_customer_balances", ReadOnly = true)]
    [Authorize(Policy = AuthPolicies.SalesAccess)]
    [Authorize(Policy = AuthPolicies.FarmReadScope)]
    [MirrorsRoute("ListCustomerBalances")]
    [Description("What each customer owes across confirmed orders: total confirmed, paid and outstanding, "
        + "largest outstanding first. Paged: when complete is false, call again with offset set to nextOffset.")]
    public async Task<McpPage<CustomerBalanceRow>> ListCustomerBalancesAsync(
        [Description("Customers per page, 1 to 100.")] int limit = McpPage.DefaultLimit,
        int offset = 0,
        CancellationToken ct = default)
    {
        // Balances carry no currency of their own; the farm's is the one orders snapshot.
        var settings = await farm.GetSettingsAsync(ct)
            ?? throw new McpException("This farm's currency could not be read.");
        McpMoney Amount(long minorUnits) => McpMoney.From(minorUnits, settings.CurrencyCode, settings.CurrencyMinorUnit);

        return await McpPage<CustomerBalanceRow>.ReadAsync(limit, offset, async (take, skip, ct) =>
            {
                var rows = await commerce.ListCustomerBalancesAsync(take, skip, ct);
                var names = await commerce.GetCustomerNamesAsync([.. rows.Select(r => r.CustomerId)], ct);
                return (IReadOnlyList<CustomerBalanceRow>)[.. rows.Select(r => new CustomerBalanceRow(
                    r.CustomerId, names.GetValueOrDefault(r.CustomerId)?.Name, Amount(r.ConfirmedTotalMinorUnits),
                    Amount(r.PaidMinorUnits), Amount(r.ConfirmedTotalMinorUnits - r.PaidMinorUnits)))];
            },
            r => r,
            ct);
    }

    private static SalesOrderRow ToRow(
        SalesOrderListItem row, IReadOnlyDictionary<Guid, CustomerReference> customers, IReadOnlyDictionary<Guid, string> gradeNames)
    {
        var order = row.Order;
        var total = order.TotalAmount;
        return new SalesOrderRow(
            order.Id, order.ReferenceNumber, order.CustomerId, customers.GetValueOrDefault(order.CustomerId)?.Name,
            order.OrderDate, order.Status.ToString(), McpMoney.From(total),
            row.OutstandingMinorUnits is { } owed ? McpMoney.From(owed, total.CurrencyCode, total.CurrencyMinorUnit) : null,
            order.DiscountReasonCode?.ToString(),
            [.. order.Items.Select(i => new SalesOrderLine(
                gradeNames.GetValueOrDefault(i.EggGradeId), i.Unit.ToString(), i.Quantity, i.QuantityBase, McpMoney.From(i.UnitPrice)))]);
    }
}

public sealed record CustomerRow(Guid CustomerId, string Name);

public sealed record SalesOrderRow(
    Guid OrderId, string Reference, Guid CustomerId, string? Customer, DateOnly OrderDate, string Status,
    McpMoney Total, McpMoney? Outstanding, string? DiscountReason, IReadOnlyList<SalesOrderLine> Lines);

public sealed record SalesOrderLine(string? Grade, string Unit, int Quantity, int Eggs, McpMoney UnitPrice);

public sealed record PaymentRow(Guid PaymentId, DateOnly PaymentDate, McpMoney Amount, string Method, bool Voided);

public sealed record CustomerBalanceRow(
    Guid CustomerId, string? Customer, McpMoney ConfirmedTotal, McpMoney Paid, McpMoney Outstanding);
