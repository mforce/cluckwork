namespace Cluckwork.Api.Endpoints.DailyEntries;

using System.Text.Json;
using Cluckwork.Api.Validation;
using Cluckwork.Application.Features.DailyEntries.AdjustDailyEntry;
using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;
using Cluckwork.Application.Features.DailyEntries.SubmitDailyEntry;
using Cluckwork.Application.Features.DailyEntries.VoidDailyEntry;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;

public static class DailyEntryEndpoints
{
    private static readonly string EntryNotFoundCode = "DailyEntry.NotFound";

    public static RouteGroupBuilder MapDailyEntryEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/", RecordDailyEntry)
            .WithName("RecordDailyEntry")
            .WithSummary("Record or update the daily production entry for a flock/house.")
            .RequireAuthorization(AuthPolicies.ProductionWrite);

        group.MapPost("/{id:guid}/submit", SubmitDailyEntry)
            .WithName("SubmitDailyEntry")
            .WithSummary("Submit a draft entry: locks it in and generates egg lots from its grade lines.")
            .RequireAuthorization(AuthPolicies.ProductionWrite);

        // Correcting or undoing submitted history is admin work (#73/#69);
        // both reconcile lots and the bird ledger in one transaction.
        group.MapPost("/{id:guid}/adjust", AdjustDailyEntry)
            .WithName("AdjustDailyEntry")
            .WithSummary("Adjust a submitted/locked entry: totals + grade lines, with lot and bird-ledger reconciliation; reason required.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        group.MapPost("/{id:guid}/void", VoidDailyEntry)
            .WithName("VoidDailyEntry")
            .WithSummary("Void a submitted entry: empties its egg lots (blocked if sold), reverses its mortality; reason required.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

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
        Cluckwork.Application.Features.Audit.IAuditEventRepository audit,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var entry = await entries.GetReadOnlyAsync(id, ct);
        if (entry is null) return Results.NotFound();
        var provenance = await audit.GetProvenanceAsync(
            nameof(Cluckwork.Domain.Eggs.DailyEntry), [id], ct);
        return Results.Ok(ToResponse(entry, provenance.GetValueOrDefault(id)));
    }

    private static async Task<IResult> ListDailyEntries(
        Cluckwork.Application.Features.DailyEntries.IDailyEntryRepository entries,
        Cluckwork.Application.Features.Audit.IAuditEventRepository audit,
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
        var provenance = await audit.GetProvenanceAsync(
            nameof(Cluckwork.Domain.Eggs.DailyEntry), list.Select(e => e.Id).ToList(), ct);
        return Results.Ok(list.Select(e => ToResponse(e, provenance.GetValueOrDefault(e.Id))));
    }

    private static DailyEntryResponse ToResponse(
        Cluckwork.Domain.Eggs.DailyEntry e,
        Cluckwork.Application.Features.Audit.EntityProvenance? p) => new(
        e.Id, e.FarmId, e.HouseId, e.FlockId, e.Date, e.Status.ToString(),
        e.TotalEggs, e.CrackedEggs, e.DirtyEggs, e.DiscardedEggs, e.MortalityCount,
        // #396 — which grade each condition counter resolved to when this entry
        // became official, or null when that condition was a loss. The SPA needs
        // it to say whether a day's cracked eggs became stock; it cannot re-derive
        // that from the current catalog, which is the whole point of the snapshot.
        e.CrackedGradeId, e.DirtyGradeId,
        e.Grades.Select(g => new GradeLineResponse(g.EggGradeId, g.Quantity)).ToList(),
        e.Version, e.AdjustReason, e.VoidReason, e.LockedAtUtc,
        // The audit snapshot is stored as JSON; embed it as an object, not a string.
        e.AdjustedFromJson is null ? null : JsonSerializer.Deserialize<JsonElement>(e.AdjustedFromJson),
        p?.CreatedByEmail, p?.CreatedAtUtc, p?.LastChangedByEmail, p?.LastChangedAtUtc);

    private static async Task<IResult> AdjustDailyEntry(
        Guid id,
        AdjustDailyEntryRequest request,
        AdjustDailyEntryHandler handler,
        IValidator<AdjustDailyEntryCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        if (request.Grades is not null && request.Grades.Any(g => g is null))
            return ValidationResponse.Problem(new Dictionary<string, string[]>
            {
                ["Grades"] = ["Grade entries must not be null."]
            });

        var command = new AdjustDailyEntryCommand(
            id, request.Version, request.TotalEggs, request.CrackedEggs, request.DirtyEggs,
            request.DiscardedEggs, request.MortalityCount, request.Reason,
            request.Grades?.Select(g => new GradeQuantityDto(g.EggGradeId, g.Quantity)).ToList());

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess ? Results.Ok(result.Value) : MapAdjustFailure(result.Error);
    }

    private static async Task<IResult> VoidDailyEntry(
        Guid id,
        VoidDailyEntryRequest request,
        VoidDailyEntryHandler handler,
        IValidator<VoidDailyEntryCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new VoidDailyEntryCommand(id, request.Version, request.Reason);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess ? Results.Ok(result.Value) : MapAdjustFailure(result.Error);
    }

    private static IResult MapAdjustFailure(Cluckwork.Domain.Common.Error error)
    {
        if (error.Code.EndsWith(".NotFound", StringComparison.Ordinal))
            return Results.NotFound();
        // A stale base version is a genuine conflict, not a validation problem.
        return error.Code == "DailyEntry.VersionMismatch"
            ? Results.Problem(error.Description, statusCode: StatusCodes.Status409Conflict, title: error.Code)
            : Results.Problem(error.Description, statusCode: 422, title: error.Code);
    }

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
            return ValidationResponse.Problem(new Dictionary<string, string[]>
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
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);

        if (result.IsSuccess)
            return Results.Created($"/api/v1/daily-entries/{result.Value}", new { Id = result.Value });
        // Unknown flock (or other missing referenced resource) is a 404, not a
        // semantic 422 — mirrors the submit endpoint's mapping.
        return result.Error.Code.EndsWith(".NotFound", StringComparison.Ordinal)
            ? Results.NotFound()
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

// Version rides on every entry so corrections send it back as their base
// (the PR #77 optimistic-concurrency contract). AdjustedFrom is the audit
// snapshot of the values the last adjustment replaced.
public sealed record DailyEntryResponse(
    Guid Id, Guid FarmId, Guid HouseId, Guid FlockId, DateOnly Date, string Status,
    int TotalEggs, int CrackedEggs, int DirtyEggs, int DiscardedEggs, int MortalityCount,
    // #396 — null on a draft (nothing has resolved yet) AND on an official entry
    // whose condition was a loss. A reader distinguishes the two by Status, not
    // by these fields.
    Guid? CrackedGradeId, Guid? DirtyGradeId,
    IReadOnlyList<GradeLineResponse> Grades,
    int Version, string? AdjustReason, string? VoidReason, DateTimeOffset? LockedAtUtc,
    JsonElement? AdjustedFrom,
    // #494 provenance, derived from the audit trail: null together for a
    // record created before that shipped (no backfill).
    string? CreatedByEmail, DateTimeOffset? CreatedAtUtc,
    string? LastChangedByEmail, DateTimeOffset? LastChangedAtUtc);

public sealed record GradeLineResponse(Guid EggGradeId, int Quantity);

public sealed record AdjustDailyEntryRequest(
    int Version, int TotalEggs, int CrackedEggs, int DirtyEggs, int DiscardedEggs,
    int MortalityCount, string Reason, List<GradeQuantityRequest>? Grades = null);

public sealed record VoidDailyEntryRequest(int Version, string Reason);
