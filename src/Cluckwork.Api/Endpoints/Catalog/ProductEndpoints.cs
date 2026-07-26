namespace Cluckwork.Api.Endpoints.Catalog;

using Cluckwork.Api.Validation;
using Cluckwork.Application.Features.Catalog;
using Cluckwork.Application.Features.Catalog.CreateProduct;
using Cluckwork.Application.Features.Catalog.SetProductActive;
using Cluckwork.Application.Features.Catalog.UpdateEggUnitConversion;
using Cluckwork.Application.Features.Catalog.UpdateProduct;
using Cluckwork.Domain.Catalog;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;

// #97 — product catalog (part 1: egg products only). Writes are configuration
// → admin-only (F19 principle); reads stay open so sales screens can render
// product names for any user (part 2).
public static class ProductEndpoints
{
    public static RouteGroupBuilder MapProductEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListProducts)
            .WithName("ListProducts")
            .WithSummary("List products with their egg-grade mapping. Active only by default; includeInactive=true adds deactivated ones.");

        group.MapPost("/", CreateProduct)
            .WithName("CreateProduct")
            .WithSummary("Create an egg product mapped to an egg grade (name unique per account, case-insensitive).")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        group.MapPut("/{id:guid}", UpdateProduct)
            .WithName("UpdateProduct")
            .WithSummary("Update a product's name, unit, default price, notes, or grade mapping. Product type is immutable.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        group.MapPost("/{id:guid}/deactivate",
                (Guid id, SetProductActiveHandler h, TenantContext t, CancellationToken ct) => SetActive(id, false, h, t, ct))
            .WithName("DeactivateProduct")
            .WithSummary("Deactivate a product: it leaves sale pickers; history is unaffected.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        group.MapPost("/{id:guid}/activate",
                (Guid id, SetProductActiveHandler h, TenantContext t, CancellationToken ct) => SetActive(id, true, h, t, ct))
            .WithName("ActivateProduct")
            .WithSummary("Reactivate a previously deactivated product.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        return group;
    }

    public static RouteGroupBuilder MapEggUnitConversionEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListConversions)
            .WithName("ListEggUnitConversions")
            .WithSummary("List the account's packed-unit definitions (eggs per dozen/tray/carton/case).");

        group.MapPut("/{id:guid}", UpdateConversion)
            .WithName("UpdateEggUnitConversion")
            .WithSummary("Redefine a packed unit's egg count. Snapshotted sale lines keep their factor; only future lines resolve the new one.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        return group;
    }

    private static async Task<IResult> ListProducts(
        IProductRepository products, TenantContext tenant, CancellationToken ct,
        bool includeInactive = false)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var list = await products.ListAsync(includeInactive, ct);
        var mappings = (await products.ListMappingsAsync(ct)).ToDictionary(m => m.ProductId, m => m.EggGradeId);
        return Results.Ok(list.Select(p => ToResponse(p, mappings.GetValueOrDefault(p.Id))));
    }

    private static async Task<IResult> CreateProduct(
        CreateProductRequest request, CreateProductHandler handler,
        IValidator<CreateProductCommand> validator, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new CreateProductCommand(
            request.Name, request.ProductType, request.DefaultUnit,
            request.DefaultPriceMinorUnits, request.EggGradeId, request.Notes);

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess
            ? Results.Created($"/api/v1/products/{result.Value}", new { Id = result.Value })
            : MapFailure(result.Error);
    }

    private static async Task<IResult> UpdateProduct(
        Guid id, UpdateProductRequest request, UpdateProductHandler handler,
        IValidator<UpdateProductCommand> validator, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new UpdateProductCommand(
            id, request.Name, request.DefaultUnit,
            request.DefaultPriceMinorUnits, request.EggGradeId, request.Notes);

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, ct);
        return result.IsSuccess ? Results.NoContent() : MapFailure(result.Error);
    }

    private static async Task<IResult> SetActive(
        Guid id, bool active, SetProductActiveHandler handler, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var result = await handler.HandleAsync(id, active, ct);
        return result.IsSuccess ? Results.NoContent() : MapFailure(result.Error);
    }

    private static async Task<IResult> ListConversions(
        IEggUnitConversionRepository conversions, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var list = await conversions.ListAsync(ct);
        return Results.Ok(list.Select(c =>
            new EggUnitConversionResponse(c.Id, c.UnitCode.ToString(), c.EggsPerUnit, c.Active, c.Version)));
    }

    private static async Task<IResult> UpdateConversion(
        Guid id, UpdateEggUnitConversionRequest request,
        UpdateEggUnitConversionHandler handler,
        IValidator<UpdateEggUnitConversionCommand> validator,
        TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new UpdateEggUnitConversionCommand(id, request.EggsPerUnit, request.Active);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, ct);
        return result.IsSuccess ? Results.NoContent() : MapFailure(result.Error);
    }

    private static IResult MapFailure(Cluckwork.Domain.Common.Error error)
    {
        if (error.Code.EndsWith(".NotFound", StringComparison.Ordinal))
            return Results.NotFound();
        return error.Code is "Product.DuplicateName" or "Product.NotActive" or "Product.AlreadyActive"
            ? Results.Problem(error.Description, statusCode: StatusCodes.Status409Conflict, title: error.Code)
            : Results.Problem(error.Description, statusCode: 422, title: error.Code);
    }

    private static ProductResponse ToResponse(Product p, Guid? eggGradeId) =>
        new(p.Id, p.Name, p.ProductType.ToString(), p.DefaultUnit.ToString(),
            p.DefaultPriceMinorUnits, p.CurrencyCode, p.CurrencyMinorUnit,
            eggGradeId, p.Notes, p.Active, p.Version);
}

public sealed record ProductResponse(
    Guid Id, string Name, string ProductType, string DefaultUnit,
    long? DefaultPriceMinorUnits, string CurrencyCode, int CurrencyMinorUnit,
    Guid? EggGradeId, string? Notes, bool Active, int Version);

public sealed record CreateProductRequest(
    string Name, string ProductType, string DefaultUnit,
    long? DefaultPriceMinorUnits, Guid? EggGradeId, string? Notes);

public sealed record UpdateProductRequest(
    string Name, string DefaultUnit,
    long? DefaultPriceMinorUnits, Guid? EggGradeId, string? Notes);

public sealed record EggUnitConversionResponse(
    Guid Id, string UnitCode, int EggsPerUnit, bool Active, int Version);

public sealed record UpdateEggUnitConversionRequest(int EggsPerUnit, bool Active);
