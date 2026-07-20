namespace Cluckwork.Api.Endpoints.Flocks;

using Cluckwork.Application.Features.Flocks;
using Cluckwork.Application.Features.Flocks.ArchiveFlock;
using Cluckwork.Application.Features.Flocks.CreateFlock;
using Cluckwork.Application.Features.Flocks.DepleteFlock;
using Cluckwork.Application.Features.Flocks.ReactivateFlock;
using Cluckwork.Application.Features.Flocks.RecordBirdMovement;
using Cluckwork.Application.Features.Flocks.UpdateFlock;
using Cluckwork.Domain.Flocks;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;

public static class FlockEndpoints
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;

    // Code produced by Error.NotFound(nameof(Flock), ...).
    private static readonly string FlockNotFoundCode = $"{nameof(Flock)}.NotFound";

    public static RouteGroupBuilder MapFlockEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/", CreateFlock)
            .WithName("CreateFlock")
            .WithSummary("Create a flock under the current account.")
            .RequireAuthorization(AuthPolicies.ProductionWrite);

        group.MapGet("/", ListFlocks)
            .WithName("ListFlocks")
            .WithSummary("List the current account's flocks (paged). Archived flocks only with includeArchived=true.");

        group.MapGet("/{id:guid}", GetFlock)
            .WithName("GetFlock")
            .WithSummary("Get a single flock by id.");

        // Corrective/lifecycle actions are admin-only (#73): they rewrite or
        // close history rather than record the day's work.
        group.MapPut("/{id:guid}", UpdateFlock)
            .WithName("UpdateFlock")
            .WithSummary("Correct a flock's name, breed, placement date, or initial count.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        group.MapPost("/{id:guid}/deplete", DepleteFlock)
            .WithName("DepleteFlock")
            .WithSummary("Mark a flock as depleted.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        group.MapPost("/{id:guid}/archive", ArchiveFlock)
            .WithName("ArchiveFlock")
            .WithSummary("Archive a flock: hidden from pickers and dashboard, visible in the management view.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        group.MapPost("/{id:guid}/reactivate", ReactivateFlock)
            .WithName("ReactivateFlock")
            .WithSummary("Undo a deplete/archive: the flock returns to Active and accepts entries for any date again.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        group.MapPost("/{id:guid}/movements", RecordMovement)
            .WithName("RecordBirdMovement")
            .WithSummary("Record a manual bird movement (Cull or Adjustment; mortality is generated from submitted daily entries).")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        group.MapGet("/{id:guid}/movements", ListMovements)
            .WithName("ListBirdMovements")
            .WithSummary("List a flock's bird movements, newest first (paged).");

        return group;
    }

    private static async Task<IResult> CreateFlock(
        CreateFlockRequest request,
        CreateFlockHandler handler,
        IValidator<CreateFlockCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new CreateFlockCommand(
            request.Name, request.Breed, request.PlacementDate, request.InitialCount);

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess
            ? Results.Created($"/api/v1/flocks/{result.Value}", new { Id = result.Value })
            : Results.Problem(result.Error.Description, statusCode: 422, title: result.Error.Code);
    }

    private static async Task<IResult> ListFlocks(
        IFlockRepository flocks, IBirdMovementRepository movements, TenantContext tenant,
        CancellationToken ct, int? limit = null, int? offset = null, bool includeArchived = false)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var skip = Math.Max(offset ?? 0, 0);

        var list = await flocks.ListAsync(take, skip, includeArchived, ct);
        var removed = await movements.RemovedByFlockAsync(ct);
        return Results.Ok(list.Select(f =>
            ToResponse(f, removed.GetValueOrDefault(f.Id, 0))));
    }

    private static async Task<IResult> GetFlock(
        Guid id, IFlockRepository flocks, IBirdMovementRepository movements,
        TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var flock = await flocks.GetByIdAsync(id, ct);
        if (flock is null) return Results.NotFound();
        var removed = await movements.RemovedForFlockAsync(id, ct);
        return Results.Ok(ToResponse(flock, removed));
    }

    private static async Task<IResult> ReactivateFlock(
        Guid id, ReactivateFlockHandler handler, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var result = await handler.HandleAsync(id, ct);
        return result.IsSuccess ? Results.NoContent() : MapFailure(result.Error);
    }

    private static async Task<IResult> RecordMovement(
        Guid id,
        RecordBirdMovementRequest request,
        RecordBirdMovementHandler handler,
        IValidator<RecordBirdMovementCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new RecordBirdMovementCommand(
            id, request.Date, request.Type, request.Quantity, request.Note);

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess
            ? Results.Created($"/api/v1/flocks/{id}/movements", new { Id = result.Value })
            : MapFailure(result.Error);
    }

    private static async Task<IResult> ListMovements(
        Guid id, IFlockRepository flocks, IBirdMovementRepository movements,
        TenantContext tenant, CancellationToken ct, int? limit = null, int? offset = null)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        // 404 for a foreign/unknown flock rather than an empty ledger.
        if (await flocks.GetByIdAsync(id, ct) is null) return Results.NotFound();

        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var skip = Math.Max(offset ?? 0, 0);

        var list = await movements.ListByFlockAsync(id, take, skip, ct);
        return Results.Ok(list.Select(m => new BirdMovementResponse(
            m.Id, m.FlockId, m.Date, m.Type.ToString(), m.Quantity, m.Note)));
    }

    private static async Task<IResult> UpdateFlock(
        Guid id,
        UpdateFlockRequest request,
        UpdateFlockHandler handler,
        IValidator<UpdateFlockCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new UpdateFlockCommand(
            id, request.Name, request.Breed, request.PlacementDate, request.InitialCount);

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, ct);
        return result.IsSuccess ? Results.NoContent() : MapFailure(result.Error);
    }

    private static async Task<IResult> DepleteFlock(
        Guid id, DepleteFlockHandler handler, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var result = await handler.HandleAsync(id, ct);
        return result.IsSuccess ? Results.NoContent() : MapFailure(result.Error);
    }

    private static async Task<IResult> ArchiveFlock(
        Guid id, ArchiveFlockHandler handler, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var result = await handler.HandleAsync(id, ct);
        return result.IsSuccess ? Results.NoContent() : MapFailure(result.Error);
    }

    private static IResult MapFailure(Cluckwork.Domain.Common.Error error)
    {
        if (error.Code == FlockNotFoundCode)
            return Results.NotFound();
        // Lifecycle mismatches (deplete a non-active flock, archive twice) are
        // conflicts with current state.
        return error.Code is "Flock.NotActive" or "Flock.AlreadyArchived" or "Flock.AlreadyActive"
            ? Results.Problem(error.Description, statusCode: StatusCodes.Status409Conflict, title: error.Code)
            : Results.Problem(error.Description, statusCode: 422, title: error.Code);
    }

    private static FlockResponse ToResponse(Flock f, long removed) => new(
        f.Id, f.FarmId, f.HouseId, f.Name, f.Breed,
        f.PlacementDate, f.InitialCount, f.InitialCount - removed, f.Status.ToString());
}

public sealed record CreateFlockRequest(
    string Name,
    string Breed,
    DateOnly PlacementDate,
    int InitialCount);

public sealed record UpdateFlockRequest(
    string Name,
    string Breed,
    DateOnly PlacementDate,
    int InitialCount);

// CurrentBirds = InitialCount − Σ movement quantities (bird ledger, #54).
public sealed record FlockResponse(
    Guid Id, Guid FarmId, Guid HouseId, string Name, string Breed,
    DateOnly PlacementDate, int InitialCount, long CurrentBirds, string Status);

public sealed record RecordBirdMovementRequest(
    DateOnly Date, string Type, int Quantity, string? Note = null);

public sealed record BirdMovementResponse(
    Guid Id, Guid FlockId, DateOnly Date, string Type, int Quantity, string? Note);
