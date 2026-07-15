namespace Cluckwork.Api.Endpoints.Flocks;

using Cluckwork.Application.Features.Flocks;
using Cluckwork.Application.Features.Flocks.CreateFlock;
using Cluckwork.Application.Features.Flocks.DepleteFlock;
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
            .WithSummary("Create a flock under the current account.");

        group.MapGet("/", ListFlocks)
            .WithName("ListFlocks")
            .WithSummary("List the current account's flocks (paged).");

        group.MapGet("/{id:guid}", GetFlock)
            .WithName("GetFlock")
            .WithSummary("Get a single flock by id.");

        group.MapPost("/{id:guid}/deplete", DepleteFlock)
            .WithName("DepleteFlock")
            .WithSummary("Mark a flock as depleted.");

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
        IFlockRepository flocks, TenantContext tenant,
        CancellationToken ct, int? limit = null, int? offset = null)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var skip = Math.Max(offset ?? 0, 0);

        var list = await flocks.ListAsync(take, skip, ct);
        return Results.Ok(list.Select(ToResponse));
    }

    private static async Task<IResult> GetFlock(
        Guid id, IFlockRepository flocks, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var flock = await flocks.GetByIdAsync(id, ct);
        return flock is null ? Results.NotFound() : Results.Ok(ToResponse(flock));
    }

    private static async Task<IResult> DepleteFlock(
        Guid id, DepleteFlockHandler handler, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var result = await handler.HandleAsync(id, ct);
        if (result.IsSuccess) return Results.NoContent();
        return result.Error.Code == FlockNotFoundCode
            ? Results.NotFound()
            : Results.Problem(result.Error.Description, statusCode: 422, title: result.Error.Code);
    }

    private static FlockResponse ToResponse(Flock f) => new(
        f.Id, f.FarmId, f.HouseId, f.Name, f.Breed,
        f.PlacementDate, f.InitialCount, f.Status.ToString());
}

public sealed record CreateFlockRequest(
    string Name,
    string Breed,
    DateOnly PlacementDate,
    int InitialCount);

public sealed record FlockResponse(
    Guid Id, Guid FarmId, Guid HouseId, string Name, string Breed,
    DateOnly PlacementDate, int InitialCount, string Status);
