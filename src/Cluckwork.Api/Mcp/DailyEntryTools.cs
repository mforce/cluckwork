using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cluckwork.Api.Middleware;
using Cluckwork.Application.Modules.EggOperations.Contracts;
using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Cluckwork.Domain.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cluckwork.Api.Mcp;

// #809 — the one write in phase 1. A model holds names, not ids, so the flock and the
// grades are addressed by name and FarmId and HouseId come from the flock. Validation,
// flock scope and eligibility stay in the validator and handler behind the contract.
// The entry stays a draft; a person submits it.
[McpServerToolType]
public sealed class DailyEntryTools(
    McpCallContext call,
    IdempotentUnit idempotency,
    IFlockLookup flocks,
    IEggOperationsModule eggs,
    IValidator<RecordDailyEntryCommand> validator)
{
    public const string RecordDailyEntry = "cluckwork_record_daily_entry";

    [McpServerTool(Name = RecordDailyEntry, Idempotent = true, OpenWorld = false)]
    [Description("Record a flock's egg production for one day as a draft daily entry. Recording again for the "
        + "same flock and date replaces the draft's numbers. A person reviews and submits the draft in Cluckwork; "
        + "until then its eggs are not in stock.")]
    [Authorize(Policy = AuthPolicies.ProductionWrite)]
    [Authorize(Policy = AuthPolicies.DailyEntriesWriteScope)]
    public async Task<RecordedDailyEntry> RecordAsync(
        [Description("A key you choose for this recording. Resending the same call with the same key returns the "
            + "first result instead of recording again; reusing it with different values is refused.")]
        string idempotencyKey,
        [Description("The flock's exact name, or its id when several flocks share the name.")] string flock,
        [Description("The production day, YYYY-MM-DD.")] DateOnly date,
        int totalEggs,
        int crackedEggs,
        int dirtyEggs,
        int discardedEggs,
        int mortalityCount,
        [Description("Sellable eggs per grade, by grade name or id.")] IReadOnlyList<GradeLine>? grades = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new McpException("idempotencyKey is required.");

        // Keyed on the tool name, so another tool cannot collide, and on the user, so two
        // people's agents choosing the same key do not refuse or replay each other.
        var scope = new IdempotencyScope(
            call.AccountId,
            Sha256($"mcp:tool:{RecordDailyEntry}"),
            Sha256($"{call.UserId}:{idempotencyKey}"),
            Sha256(JsonSerializer.Serialize(new
            {
                flock, date, totalEggs, crackedEggs, dirtyEggs, discardedEggs, mortalityCount, grades,
            })));

        var result = await idempotency.RunAsync(scope, async () =>
        {
            var recorded = await RecordOnceAsync(
                flock, date, totalEggs, crackedEggs, dirtyEggs, discardedEggs, mortalityCount, grades, ct);
            return recorded.IsSuccess
                ? new IdempotentResponse(true, StatusCodes.Status200OK, "application/json", JsonSerializer.Serialize(recorded.Value))
                : new IdempotentResponse(false, StatusCodes.Status422UnprocessableEntity, "text/plain",
                    $"{recorded.Error.Code}: {recorded.Error.Description}");
        }, ct);

        return result switch
        {
            IdempotentResult.Executed { Response.Succeeded: true } executed => Deserialize(executed.Response),
            IdempotentResult.Executed failed => throw new McpException(failed.Response.Body),
            IdempotentResult.Replayed replayed => Deserialize(replayed.Response) with { Replayed = true },
            IdempotentResult.KeyReused => throw new McpException(
                "Idempotency.KeyReused: this idempotencyKey was already used with different values. "
                + "Use a new key for a new recording."),
            _ => throw new McpException(
                "Idempotency.InProgress: a call with this idempotencyKey is still running. Retry shortly."),
        };
    }

    private async Task<Result<RecordedDailyEntry>> RecordOnceAsync(
        string flock, DateOnly date, int totalEggs, int crackedEggs, int dirtyEggs, int discardedEggs,
        int mortalityCount, IReadOnlyList<GradeLine>? grades, CancellationToken ct)
    {
        var resolved = await ResolveFlockAsync(flock, ct);
        if (resolved.IsFailure)
            return Result.Failure<RecordedDailyEntry>(resolved.Error);
        var details = resolved.Value;

        List<GradeQuantityDto>? gradeLines = null;
        if (grades is not null)
        {
            var lines = await ResolveGradesAsync(details.FarmId, grades, ct);
            if (lines.IsFailure)
                return Result.Failure<RecordedDailyEntry>(lines.Error);
            gradeLines = lines.Value;
        }

        var command = new RecordDailyEntryCommand(
            details.FarmId, details.HouseId, details.Id, date,
            totalEggs, crackedEggs, dirtyEggs, discardedEggs, mortalityCount, gradeLines);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Result.Failure<RecordedDailyEntry>(Error.Validation("DailyEntry.Invalid",
                string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));

        var id = await eggs.RecordDailyEntryAsync(command, call.AccountId, ct);
        return id.IsSuccess
            ? Result.Success(new RecordedDailyEntry(id.Value, details.Id, details.Name, date, Replayed: false))
            : Result.Failure<RecordedDailyEntry>(id.Error);
    }

    // Exactly one accessible match (guard 32). Duplicate names are legal, so a shared
    // name is refused with the candidates, and the caller sends an id.
    private async Task<Result<FlockDetails>> ResolveFlockAsync(string flock, CancellationToken ct)
    {
        if (!Guid.TryParse(flock, out var id))
        {
            switch (await flocks.ResolveByNameAsync(flock, ct))
            {
                case FlockNameResolution.Found found:
                    id = found.Flock.Id;
                    break;
                case FlockNameResolution.Ambiguous ambiguous:
                    var candidates = new List<string>();
                    foreach (var candidate in ambiguous.Candidates)
                    {
                        var placed = (await flocks.GetAsync(candidate.Id, ct))?.PlacementDate;
                        candidates.Add($"{candidate.Id} ({candidate.Status}, placed {placed:yyyy-MM-dd})");
                    }
                    return Result.Failure<FlockDetails>(Error.Validation("Flock.Ambiguous",
                        $"{candidates.Count} flocks are named '{flock}'. Send the id of one: {string.Join("; ", candidates)}."));
                default:
                    return Result.Failure<FlockDetails>(FlockNotFound(flock));
            }
        }

        var details = await flocks.GetAsync(id, ct);
        return details is null ? Result.Failure<FlockDetails>(FlockNotFound(flock)) : Result.Success(details);
    }

    private async Task<Result<List<GradeQuantityDto>>> ResolveGradesAsync(
        Guid farmId, IReadOnlyList<GradeLine> grades, CancellationToken ct)
    {
        var active = await eggs.ListActiveGradesAsync(farmId, ct);
        var lines = new List<GradeQuantityDto>();
        foreach (var line in grades)
        {
            // Grade names are unique per farm, ignoring case (IX_EggGrades_AccountId_FarmId_LowerName).
            var grade = line is null ? null : active.FirstOrDefault(g => Guid.TryParse(line.Grade, out var gradeId)
                ? g.Id == gradeId
                : string.Equals(g.Name, line.Grade, StringComparison.OrdinalIgnoreCase));
            if (grade is null)
                return Result.Failure<List<GradeQuantityDto>>(Error.Validation("DailyEntry.UnknownGrade",
                    $"No active grade '{line?.Grade}'. Active grades: {string.Join(", ", active.Select(g => g.Name))}."));
            lines.Add(new GradeQuantityDto(grade.Id, line!.Quantity));
        }

        return Result.Success(lines);
    }

    private static Error FlockNotFound(string flock) =>
        Error.NotFound("Flock", flock) with { Description = $"No flock '{flock}' that you can record for." };

    private static RecordedDailyEntry Deserialize(IdempotentResponse response) =>
        JsonSerializer.Deserialize<RecordedDailyEntry>(response.Body)!;

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record GradeLine(
    [property: Description("The grade's name or id.")] string Grade,
    int Quantity);

public sealed record RecordedDailyEntry(Guid DailyEntryId, Guid FlockId, string FlockName, DateOnly Date, bool Replayed);
