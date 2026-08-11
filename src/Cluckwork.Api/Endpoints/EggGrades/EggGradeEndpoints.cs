namespace Cluckwork.Api.Endpoints.EggGrades;

using Cluckwork.Api.Validation;
using Cluckwork.Application.Features.Audit;
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
        IEggGradeRepository grades, IAuditEventRepository audit,
        TenantContext tenant, CancellationToken ct,
        bool includeInactive = false)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var list = includeInactive
            ? await grades.ListAllAsync(ct)
            : await grades.ListActiveAsync(farmId: null, ct);
        // This list is unpaginated, so the batch is however many grades the
        // farm has; the lookup chunks internally rather than refusing a big one.
        var provenance = await audit.GetProvenanceAsync(
            nameof(EggGrade), list.Select(g => g.Id).ToList(), ct);
        return Results.Ok(list.Select(g => ToResponse(g, provenance.GetValueOrDefault(g.Id))));
    }

    private static async Task<IResult> GetEggGrade(
        Guid id, IEggGradeRepository grades, IAuditEventRepository audit,
        TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var grade = await grades.GetByIdAsync(id, ct);
        if (grade is null) return Results.NotFound();
        var provenance = await audit.GetProvenanceAsync(nameof(EggGrade), [id], ct);
        return Results.Ok(ToResponse(grade, provenance.GetValueOrDefault(id)));
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
            return ValidationResponse.Problem(validation);

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
            return ValidationResponse.Problem(validation);

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

    private static EggGradeResponse ToResponse(EggGrade g, EntityProvenance? p) =>
        new(g.Id, g.FarmId, g.Name, g.GradeType.ToString(), g.SortOrder, g.IsSaleable,
            g.DailyEntryKind.ToString(), g.Active,
            p?.CreatedByEmail, p?.CreatedAtUtc, p?.LastChangedByEmail, p?.LastChangedAtUtc);
}

// FarmId included from day one — grades are farm-scoped (spec §9.1) and a
// multi-farm client needs to know which farm each bucket belongs to.
public sealed record EggGradeResponse(
    Guid Id, Guid FarmId, string Name, string GradeType, int SortOrder, bool IsSaleable,
    // #396 — "Manual", "Cracked" or "Dirty". The client cannot infer this from
    // Name (the farm may rename a grade) or from GradeType (a farm can have many
    // Quality grades, only one of which is the Cracked counter's). It is what
    // lets the Grading pane leave the two counter-fed grades out; the server
    // refuses them regardless, so this is the affordance, not the enforcement.
    string DailyEntryKind,
    bool Active,
    // #494 provenance, derived from the audit trail: null together for a
    // record created before that shipped (no backfill).
    string? CreatedByEmail, DateTimeOffset? CreatedAtUtc,
    string? LastChangedByEmail, DateTimeOffset? LastChangedAtUtc);

public sealed record CreateEggGradeRequest(
    string Name, string GradeType, int SortOrder, bool IsSaleable);

public sealed record UpdateEggGradeRequest(
    string Name, int SortOrder, bool IsSaleable);
