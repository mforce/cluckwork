namespace Cluckwork.Api.Endpoints.EggGrades;

using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.EggGrades.CreateEggGrade;
using Cluckwork.Application.Features.EggGrades.SetEggGradeActive;
using Cluckwork.Application.Features.EggGrades.UpdateEggGrade;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;

public static class EggGradeEndpoints
{
    public static RouteGroupBuilder MapEggGradeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListEggGrades)
            .WithName("ListEggGrades")
            .WithSummary("List egg grades. Active only by default; includeInactive=true adds deactivated grades (management view).");

        group.MapGet("/{id:guid}", GetEggGrade)
            .WithName("GetEggGrade")
            .WithSummary("Get a single egg grade by id (active or not).");

        // The grade catalog is configuration — admin-only (#73); reads stay open
        // so capture screens can render names for any user.
        group.MapPost("/", CreateEggGrade)
            .WithName("CreateEggGrade")
            .WithSummary("Create an egg grade (name unique per farm, case-insensitive).")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        group.MapPut("/{id:guid}", UpdateEggGrade)
            .WithName("UpdateEggGrade")
            .WithSummary("Rename a grade or change its sort order / saleability. Grade type is immutable.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        group.MapPost("/{id:guid}/deactivate", (Guid id, SetEggGradeActiveHandler h, TenantContext t, CancellationToken ct) => SetActive(id, false, h, t, ct))
            .WithName("DeactivateEggGrade")
            .WithSummary("Deactivate a grade: it leaves capture/order pickers; existing stock and history are unaffected.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        group.MapPost("/{id:guid}/activate", (Guid id, SetEggGradeActiveHandler h, TenantContext t, CancellationToken ct) => SetActive(id, true, h, t, ct))
            .WithName("ActivateEggGrade")
            .WithSummary("Reactivate a previously deactivated grade.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        return group;
    }

    private static async Task<IResult> ListEggGrades(
        IEggGradeRepository grades, TenantContext tenant, CancellationToken ct,
        bool includeInactive = false)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var list = includeInactive
            ? await grades.ListAllAsync(ct)
            : await grades.ListActiveAsync(farmId: null, ct);
        return Results.Ok(list.Select(ToResponse));
    }

    private static async Task<IResult> GetEggGrade(
        Guid id, IEggGradeRepository grades, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var grade = await grades.GetByIdAsync(id, ct);
        return grade is null ? Results.NotFound() : Results.Ok(ToResponse(grade));
    }

    private static async Task<IResult> CreateEggGrade(
        CreateEggGradeRequest request,
        CreateEggGradeHandler handler,
        IValidator<CreateEggGradeCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new CreateEggGradeCommand(
            request.Name, request.GradeType, request.SortOrder, request.IsSaleable);

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess
            ? Results.Created($"/api/v1/egg-grades/{result.Value}", new { Id = result.Value })
            : MapFailure(result.Error);
    }

    private static async Task<IResult> UpdateEggGrade(
        Guid id,
        UpdateEggGradeRequest request,
        UpdateEggGradeHandler handler,
        IValidator<UpdateEggGradeCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new UpdateEggGradeCommand(id, request.Name, request.SortOrder, request.IsSaleable);

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, ct);
        return result.IsSuccess ? Results.NoContent() : MapFailure(result.Error);
    }

    private static async Task<IResult> SetActive(
        Guid id, bool active, SetEggGradeActiveHandler handler, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var result = await handler.HandleAsync(id, active, ct);
        return result.IsSuccess ? Results.NoContent() : MapFailure(result.Error);
    }

    private static IResult MapFailure(Cluckwork.Domain.Common.Error error)
    {
        if (error.Code.EndsWith(".NotFound", StringComparison.Ordinal))
            return Results.NotFound();
        // Duplicate names and activate/deactivate state mismatches are conflicts
        // with current state, not validation problems.
        return error.Code is "EggGrade.DuplicateName" or "EggGrade.NotActive" or "EggGrade.AlreadyActive"
            ? Results.Problem(error.Description, statusCode: StatusCodes.Status409Conflict, title: error.Code)
            : Results.Problem(error.Description, statusCode: 422, title: error.Code);
    }

    private static EggGradeResponse ToResponse(EggGrade g) =>
        new(g.Id, g.FarmId, g.Name, g.GradeType.ToString(), g.SortOrder, g.IsSaleable, g.Active);
}

// FarmId included from day one — grades are farm-scoped (spec §9.1) and a
// multi-farm client needs to know which farm each bucket belongs to.
public sealed record EggGradeResponse(
    Guid Id, Guid FarmId, string Name, string GradeType, int SortOrder, bool IsSaleable, bool Active);

public sealed record CreateEggGradeRequest(
    string Name, string GradeType, int SortOrder, bool IsSaleable);

public sealed record UpdateEggGradeRequest(
    string Name, int SortOrder, bool IsSaleable);
