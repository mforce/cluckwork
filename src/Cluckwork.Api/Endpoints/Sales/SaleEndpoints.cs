namespace Cluckwork.Api.Endpoints.Sales;

using Cluckwork.Application.Features.Sales.ConfirmSale;
using Cluckwork.Infrastructure.Persistence;

public static class SaleEndpoints
{
    public static RouteGroupBuilder MapSaleEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/confirm", ConfirmSale)
            .WithName("ConfirmSale")
            .WithSummary("Confirm a sales order and allocate egg lots via FIFO (online-only).");

        return group;
    }

    private static async Task<IResult> ConfirmSale(
        Guid id,
        ConfirmSaleHandler handler,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved)
            return Results.Unauthorized();

        var result = await handler.HandleAsync(new ConfirmSaleCommand(id), tenant.AccountId, ct);

        // TenantMismatch is surfaced as NotFound to avoid revealing that the
        // resource exists but belongs to a different tenant.
        if (!result.IsSuccess)
        {
            if (result.Error.Code.EndsWith(".NotFound") || result.Error.Code == "Tenant.Mismatch")
                return Results.NotFound();

            // SalesOrder.NotDraft is a genuine state conflict (409); all other domain
            // errors (insufficient stock, withdrawal restriction, no items) are
            // business-rule violations that belong on 422 Unprocessable Entity.
            var status = result.Error.Code == "SalesOrder.NotDraft"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status422UnprocessableEntity;

            return Results.Problem(result.Error.Description, statusCode: status, title: result.Error.Code);
        }

        return Results.Ok(result.Value);
    }
}
