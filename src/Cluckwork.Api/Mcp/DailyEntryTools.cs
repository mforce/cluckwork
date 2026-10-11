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

// Daily production entries. #807 reads them; the adjust and void reasons are farm-entered
// text and are left out (#792). #809 records one, the only write in phase 1: a model holds
// names, not ids, so the flock and the grades are addressed by name and FarmId and HouseId
// come from the flock. Validation, flock scope and eligibility stay in the validator and
// handler behind the contract. The entry stays a draft; a person submits it.
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
        + "same flock and date replaces the draft's numbers; leave grades out to keep its grade lines. A submitted "
        + "entry cannot be changed here. A person reviews and submits the draft in Cluckwork; "
        + "until then its eggs are not in stock.")]
    [Authorize(Policy = AuthPolicies.ProductionWrite)]
    [Authorize(Policy = AuthPolicies.DailyEntriesWriteScope)]
    [MirrorsRoute("RecordDailyEntry")]
    public async Task<RecordedDailyEntry> RecordAsync(
        [Description("A key you choose for this recording. A successful result is kept for at least 48 hours. "
            + "Until then, resending the same call with the same key returns that result instead of recording again, "
            + "and reusing the key with different values is refused. After 48 hours the key may be purged and a retry "
            + "may record again. Before retrying an older call, check the current entry with "
            + "cluckwork_list_daily_entries, or ask a person when this connection cannot read.")]
        string idempotencyKey,
        [Description("The flock's exact name, or its id when several flocks share the name.")] string flock,
        [Description("The production day, YYYY-MM-DD.")] DateOnly date,
        int totalEggs,
        int crackedEggs,
        int dirtyEggs,
        int discardedEggs,
        int mortalityCount,
        [Description("Sellable eggs per grade, by grade name or id. Leave it out to keep the draft's existing grade "
            + "lines; an empty list removes them.")] IReadOnlyList<GradeLine>? grades = null,
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
                    Refusal(recorded.Error));
        }, ct);

        // A replay never runs RecordOnceAsync, so the caller's current flock access is checked
        // here. A Worker unassigned since the first call gets the refusal a fresh call gets.
        if (result is IdempotentResult.Replayed replayed)
        {
            var reply = Deserialize(replayed.Response);
            if (await flocks.GetAsync(reply.FlockId, ct) is null)
                throw FlockTools.NotFound(flock);
            return reply with { Replayed = true };
        }

        return result switch
        {
            IdempotentResult.Executed { Response.Succeeded: true } executed => Deserialize(executed.Response),
            IdempotentResult.Executed failed => throw new McpException(failed.Response.Body),
            IdempotentResult.KeyReused => throw new McpException(
                "Idempotency.KeyReused: this idempotencyKey was already used with different values. "
                + "Use a new key for a new recording."),
            _ => throw new McpException(
                "Idempotency.InProgress: a call with this idempotencyKey is still running. Retry shortly."),
        };
    }

    [McpServerTool(Name = "cluckwork_list_daily_entries", ReadOnly = true, OpenWorld = false)]
    [Authorize]
    [Authorize(Policy = AuthPolicies.FarmReadScope)]
    [MirrorsRoute("ListDailyEntries")]
    [Description("Daily production entries, newest first: eggs collected by grade, cracked, dirty and discarded eggs, "
        + "and mortality, per flock and day. Paged: when complete is false, call again with offset set to nextOffset.")]
    public async Task<McpPage<DailyEntryRow>> ListDailyEntriesAsync(
        [Description("Only this flock's entries, by name or id. A name shared by several flocks is refused with their ids.")]
        string? flock = null,
        [Description("Earliest day, yyyy-mm-dd.")] DateOnly? from = null,
        [Description("Latest day, yyyy-mm-dd.")] DateOnly? to = null,
        [Description("Entries per page, 1 to 100.")] int limit = McpPage.DefaultLimit,
        int offset = 0,
        CancellationToken ct = default)
    {
        var flockId = flock is null ? (Guid?)null : (await FlockTools.ResolveAsync(flocks, flock, ct)).Id;
        var grades = (await eggs.ListAllGradesAsync(ct)).ToDictionary(g => g.Id, g => g.Name);
        return await McpPage<DailyEntryRow>.ReadAsync(limit, offset, async (take, skip, ct) =>
            {
                var rows = await eggs.ListDailyEntriesAsync(flockId, from, to, take, skip, ct);
                var names = await flocks.GetDisplayNamesAsync([.. rows.Select(e => e.FlockId).Distinct()], ct);
                return (IReadOnlyList<(DailyEntryDetails Entry, string? FlockName)>)
                    [.. rows.Select(e => (e, names.GetValueOrDefault(e.FlockId)?.Name))];
            },
            r => new DailyEntryRow(
                r.Entry.Id, r.Entry.FlockId, r.FlockName, r.Entry.Date, r.Entry.Status.ToString(),
                r.Entry.TotalEggs, r.Entry.CrackedEggs, r.Entry.DirtyEggs, r.Entry.DiscardedEggs, r.Entry.MortalityCount,
                [.. r.Entry.Grades.Select(g => new GradeCount(grades.GetValueOrDefault(g.EggGradeId), g.Quantity))]),
            ct);
    }

    private async Task<Result<RecordedDailyEntry>> RecordOnceAsync(
        string flock, DateOnly date, int totalEggs, int crackedEggs, int dirtyEggs, int discardedEggs,
        int mortalityCount, IReadOnlyList<GradeLine>? grades, CancellationToken ct)
    {
        var details = await ResolveFlockAsync(flock, ct);

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

    // Exactly one accessible match (guard 32), through the read tools' resolver. A shared name
    // is refused with candidate ids and never picked; the refusal releases the claim.
    private async Task<FlockDetails> ResolveFlockAsync(string flock, CancellationToken ct) =>
        await flocks.GetAsync((await FlockTools.ResolveAsync(flocks, flock, ct)).Id, ct)
            ?? throw FlockTools.NotFound(flock);

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

    private static string Refusal(Error error) => $"{error.Code}: {error.Description}";

    private static RecordedDailyEntry Deserialize(IdempotentResponse response) =>
        JsonSerializer.Deserialize<RecordedDailyEntry>(response.Body)!;

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record GradeLine(
    [property: Description("The grade's name or id.")] string Grade,
    int Quantity);

public sealed record RecordedDailyEntry(Guid DailyEntryId, Guid FlockId, string FlockName, DateOnly Date, bool Replayed);

public sealed record DailyEntryRow(
    Guid DailyEntryId, Guid FlockId, string? FlockName, DateOnly Date, string Status,
    int TotalEggs, int CrackedEggs, int DirtyEggs, int DiscardedEggs, int MortalityCount,
    IReadOnlyList<GradeCount> Grades);

public sealed record GradeCount(string? Grade, int Quantity);
