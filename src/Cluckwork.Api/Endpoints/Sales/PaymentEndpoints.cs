namespace Cluckwork.Api.Endpoints.Sales;

using Cluckwork.Application.Features.Sales;
using Cluckwork.Application.Features.Sales.RecordPayment;
using Cluckwork.Application.Features.Sales.VoidPayment;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;

// Customer payments (#89). Money data — every route here is AdminOnly, reads
// included (the #87 money/production split).
public static class PaymentEndpoints
{
    // Mounted on the /sales group: order-scoped settlement history + record.
    public static RouteGroupBuilder MapOrderPaymentEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}/payments", ListOrderPayments)
            .WithName("ListOrderPayments")
            .WithSummary("List an order's payments (voided included) with paid/outstanding totals.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        group.MapPost("/{id:guid}/payments", RecordPayment)
            .WithName("RecordPayment")
            .WithSummary("Record a payment against a confirmed order. Currency copies from the order; overpaying the outstanding amount is refused.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        return group;
    }

    // Own /payments group: cross-order actions.
    public static RouteGroupBuilder MapPaymentEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/void", VoidPayment)
            .WithName("VoidPayment")
            .WithSummary("Void a mistaken payment (reason required; base version; the row is kept).");

        return group;
    }

    // Mounted on the /customers group.
    public static RouteGroupBuilder MapCustomerBalanceEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/balances", ListCustomerBalances)
            .WithName("ListCustomerBalances")
            .WithSummary("Per-customer confirmed totals, settled payments, and outstanding balance (server-side sums).")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        return group;
    }

    private static async Task<IResult> ListOrderPayments(
        Guid id,
        IPaymentRepository payments,
        ISalesOrderRepository orders,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var order = await orders.GetReadOnlyAsync(id, ct);
        if (order is null) return Results.NotFound();

        var list = await payments.ListByOrderAsync(id, ct);
        var paid = list.Where(p => !p.Voided).Sum(p => p.AmountMinorUnits);
        return Results.Ok(new OrderPaymentsResponse(
            list.Select(ToResponse).ToList(),
            paid,
            order.TotalAmount.MinorUnits - paid,
            order.TotalAmount.MinorUnits,
            order.TotalAmount.CurrencyCode,
            order.TotalAmount.CurrencyMinorUnit));
    }

    private static async Task<IResult> RecordPayment(
        Guid id,
        RecordPaymentRequest request,
        RecordPaymentHandler handler,
        IValidator<RecordPaymentCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new RecordPaymentCommand(
            id, request.PaymentDate, request.AmountMinorUnits,
            request.Method, request.ReferenceNumber, request.Note);

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess
            ? Results.Created($"/api/v1/sales/{id}/payments", new { Id = result.Value })
            : MapFailure(result.Error);
    }

    private static async Task<IResult> VoidPayment(
        Guid id,
        VoidPaymentRequest request,
        VoidPaymentHandler handler,
        IPaymentRepository payments,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var result = await handler.HandleAsync(
            new VoidPaymentCommand(id, request.Version, request.Reason), ct);
        if (result.IsFailure) return MapFailure(result.Error);

        var updated = await payments.GetByIdAsync(id, ct);
        return updated is null ? Results.NotFound() : Results.Ok(ToResponse(updated));
    }

    private static async Task<IResult> ListCustomerBalances(
        IPaymentRepository payments,
        Cluckwork.Application.Features.Accounts.IAccountRepository accounts,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var rows = await payments.ListCustomerBalancesAsync(ct);
        var account = await accounts.GetCurrentAsync(ct);
        return Results.Ok(new CustomerBalancesResponse(
            rows.Select(r => new CustomerBalanceResponse(
                r.CustomerId, r.ConfirmedTotalMinorUnits, r.PaidMinorUnits,
                r.ConfirmedTotalMinorUnits - r.PaidMinorUnits)).ToList(),
            account?.DefaultCurrencyCode ?? "",
            account?.DefaultCurrencyMinorUnit ?? 2));
    }

    private static IResult MapFailure(Cluckwork.Domain.Common.Error error)
    {
        if (error.Code.EndsWith(".NotFound", StringComparison.Ordinal))
            return Results.NotFound();
        return error.Code is "Payment.VersionMismatch" or "Payment.AlreadyVoided"
            ? Results.Problem(error.Description, statusCode: StatusCodes.Status409Conflict, title: error.Code)
            : Results.Problem(error.Description, statusCode: 422, title: error.Code);
    }

    private static PaymentResponse ToResponse(Payment p) =>
        new(p.Id, p.SalesOrderId, p.CustomerId, p.PaymentDate, p.AmountMinorUnits,
            p.CurrencyCode, p.CurrencyMinorUnit, p.Method.ToString(),
            p.ReferenceNumber, p.Note, p.Voided, p.VoidReason, p.Version);
}

public sealed record PaymentResponse(
    Guid Id, Guid SalesOrderId, Guid CustomerId, DateOnly PaymentDate,
    long AmountMinorUnits, string CurrencyCode, int CurrencyMinorUnit,
    string Method, string? ReferenceNumber, string? Note,
    bool Voided, string? VoidReason, int Version);

public sealed record OrderPaymentsResponse(
    List<PaymentResponse> Items, long PaidMinorUnits, long OutstandingMinorUnits,
    long TotalMinorUnits, string CurrencyCode, int CurrencyMinorUnit);

public sealed record RecordPaymentRequest(
    DateOnly PaymentDate, long AmountMinorUnits, string Method,
    string? ReferenceNumber, string? Note);

public sealed record VoidPaymentRequest(int Version, string Reason);

public sealed record CustomerBalanceResponse(
    Guid CustomerId, long ConfirmedTotalMinorUnits, long PaidMinorUnits, long OutstandingMinorUnits);

public sealed record CustomerBalancesResponse(
    List<CustomerBalanceResponse> Items, string CurrencyCode, int CurrencyMinorUnit);
