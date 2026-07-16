namespace Cluckwork.Api.Endpoints.DailyEntries;

using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;
using Cluckwork.Application.Features.DailyEntries.SubmitDailyEntry;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;

public static class DailyEntryEndpoints
{
    private static readonly string EntryNotFoundCode = "DailyEntry.NotFound";

    public static RouteGroupBuilder MapDailyEntryEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/", RecordDailyEntry)
            .WithName("RecordDailyEntry")
            .WithSummary("Record or update the daily production entry for a flock/house.");

        group.MapPost("/{id:guid}/submit", SubmitDailyEntry)
            .WithName("SubmitDailyEntry")
            .WithSummary("Submit a draft entry: locks it in and generates egg lots from its grade lines.");

        group.MapGet("/{id:guid}", GetDailyEntry)
            .WithName("GetDailyEntry")
            .WithSummary("Get a daily entry with its grade lines.");

        group.MapGet("/", ListDailyEntries)
            .WithName("ListDailyEntries")
            .WithSummary("List daily entries, newest first (optional flock/date filters, paged).");

        return group;
    }

    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;

    private static async Task<IResult> GetDailyEntry(
        Guid id,
        Cluckwork.Application.Features.DailyEntries.IDailyEntryRepository entries,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var entry = await entries.GetReadOnlyAsync(id, ct);
        return entry is null ? Results.NotFound() : Results.Ok(ToResponse(entry));
    }

    private static async Task<IResult> ListDailyEntries(
        Cluckwork.Application.Features.DailyEntries.IDailyEntryRepository entries,
        TenantContext tenant,
        CancellationToken ct,
        Guid? flockId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        int? limit = null,
        int? offset = null)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var skip = Math.Max(offset ?? 0, 0);

        var list = await entries.ListAsync(flockId, from, to, take, skip, ct);
        return Results.Ok(list.Select(ToResponse));
    }

    private static DailyEntryResponse ToResponse(Cluckwork.Domain.Eggs.DailyEntry e) => new(
        e.Id, e.FarmId, e.HouseId, e.FlockId, e.Date, e.Status.ToString(),
        e.TotalEggs, e.CrackedEggs, e.DirtyEggs, e.DiscardedEggs, e.MortalityCount,
        e.Grades.Select(g => new GradeLineResponse(g.EggGradeId, g.Quantity)).ToList());

    private static async Task<IResult> SubmitDailyEntry(
        Guid id,
        SubmitDailyEntryHandler handler,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var result = await handler.HandleAsync(id, tenant.AccountId, ct);
        if (result.IsSuccess) return Results.Ok(result.Value);
        return result.Error.Code == EntryNotFoundCode
            ? Results.NotFound()
            : Results.Problem(result.Error.Description, statusCode: 422, title: result.Error.Code);
    }

    private static async Task<IResult> RecordDailyEntry(
        RecordDailyEntryRequest request,
        RecordDailyEntryHandler handler,
        IValidator<RecordDailyEntryCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved)
            return Results.Unauthorized();

        // System.Text.Json can bind "grades": [null] into the list despite the
        // non-nullable element type; reject it here instead of NRE-ing in mapping.
        if (request.Grades is not null && request.Grades.Any(g => g is null))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Grades"] = ["Grade entries must not be null."]
            });

        var command = new RecordDailyEntryCommand(
            request.FarmId, request.HouseId, request.FlockId, request.Date,
            request.TotalEggs, request.CrackedEggs, request.DirtyEggs,
            request.DiscardedEggs, request.MortalityCount,
            request.Grades?.Select(g => new GradeQuantityDto(g.EggGradeId, g.Quantity)).ToList());

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);

        return result.IsSuccess
            ? Results.Created($"/api/v1/daily-entries/{result.Value}", new { Id = result.Value })
            : Results.Problem(result.Error.Description, statusCode: 422, title: result.Error.Code);
    }
}

public sealed record RecordDailyEntryRequest(
    Guid FarmId, Guid HouseId, Guid FlockId, DateOnly Date,
    int TotalEggs, int CrackedEggs, int DirtyEggs, int DiscardedEggs, int MortalityCount,
    List<GradeQuantityRequest>? Grades = null);

// Sellable production for one grade; eggGradeId references a row from
// GET /api/v1/egg-grades. Contract: omitted/null = leave existing grade lines
// unchanged (older clients); [] = explicitly clear all lines. #8 turns these
// lines into egg lots.
public sealed record GradeQuantityRequest(Guid EggGradeId, int Quantity);

public sealed record DailyEntryResponse(
    Guid Id, Guid FarmId, Guid HouseId, Guid FlockId, DateOnly Date, string Status,
    int TotalEggs, int CrackedEggs, int DirtyEggs, int DiscardedEggs, int MortalityCount,
    IReadOnlyList<GradeLineResponse> Grades);

public sealed record GradeLineResponse(Guid EggGradeId, int Quantity);
