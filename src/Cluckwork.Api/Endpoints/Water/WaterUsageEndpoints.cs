namespace Cluckwork.Api.Endpoints.Water;

using Cluckwork.Application.Features.Inventory;
using Cluckwork.Application.Features.Inventory.RecordWaterUsage;
using Cluckwork.Application.Features.Inventory.UpdateWaterUsage;
using Cluckwork.Domain.Inventory;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;

public static class WaterUsageEndpoints
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;

    public static RouteGroupBuilder MapWaterUsageEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/", RecordWaterUsage)
            .WithName("RecordWaterUsage")
            .WithSummary("Record water consumed by a flock (direct quantity or meter readings).");

        group.MapPut("/{id:guid}", UpdateWaterUsage)
            .WithName("UpdateWaterUsage")
            .WithSummary("Correct a water record (quantity/source/meters/note; flock and date are fixed).");

        group.MapGet("/", ListWaterUsage)
            .WithName("ListWaterUsage")
            .WithSummary("List water records, newest first (optional flock/date filters, paged).");

        return group;
    }

    private static async Task<IResult> RecordWaterUsage(
        RecordWaterUsageRequest request,
        RecordWaterUsageHandler handler,
        IValidator<RecordWaterUsageCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new RecordWaterUsageCommand(
            request.FlockId, request.Date, request.Quantity, request.Unit,
            request.Source, request.MeterStart, request.MeterEnd, request.Note);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess
            ? Results.Created("/api/v1/water-usage", new { Id = result.Value })
            : MapFailure(result.Error);
    }

    private static async Task<IResult> UpdateWaterUsage(
        Guid id,
        UpdateWaterUsageRequest request,
        UpdateWaterUsageHandler handler,
        IValidator<UpdateWaterUsageCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new UpdateWaterUsageCommand(
            id, request.Version, request.Quantity, request.Unit, request.Source,
            request.MeterStart, request.MeterEnd, request.Note);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, ct);
        return result.IsSuccess ? Results.NoContent() : MapFailure(result.Error);
    }

    private static async Task<IResult> ListWaterUsage(
        IWaterUsageRepository waterUsages, TenantContext tenant, CancellationToken ct,
        Guid? flockId = null, DateOnly? from = null, DateOnly? to = null,
        int? limit = null, int? offset = null)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var skip = Math.Max(offset ?? 0, 0);
        var list = await waterUsages.ListAsync(flockId, from, to, take, skip, ct);
        return Results.Ok(list.Select(u => new WaterUsageResponse(
            u.Id, u.FlockId, u.Date, u.Quantity, u.Unit, u.Source.ToString(),
            u.MeterStart, u.MeterEnd, u.Note, u.Version)));
    }

    private static IResult MapFailure(Cluckwork.Domain.Common.Error error)
    {
        if (error.Code.EndsWith(".NotFound", StringComparison.Ordinal))
            return Results.NotFound();
        // A stale base version is a genuine conflict, not a validation problem.
        return error.Code == "WaterUsage.VersionMismatch"
            ? Results.Problem(error.Description, statusCode: StatusCodes.Status409Conflict, title: error.Code)
            : Results.Problem(error.Description, statusCode: 422, title: error.Code);
    }
}

// Version rides on every row so corrections send it back as their base —
// the mismatch → 409 contract (stale form never silently overwrites).
public sealed record WaterUsageResponse(
    Guid Id, Guid FlockId, DateOnly Date, decimal Quantity, string Unit, string Source,
    decimal? MeterStart, decimal? MeterEnd, string? Note, int Version);

public sealed record RecordWaterUsageRequest(
    Guid FlockId, DateOnly Date, decimal? Quantity, string? Unit, string Source,
    decimal? MeterStart, decimal? MeterEnd, string? Note);

public sealed record UpdateWaterUsageRequest(
    int Version, decimal? Quantity, string? Unit, string Source,
    decimal? MeterStart, decimal? MeterEnd, string? Note);
