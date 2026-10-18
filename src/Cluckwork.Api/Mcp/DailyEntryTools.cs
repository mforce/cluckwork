using System.ComponentModel;
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
// handler behind the contract. The entry stays a draft until a person submits it, or an app
// allowed to submit calls the submit tool (#1311), which runs the REST route's handler.
[McpServerToolType]
public sealed class DailyEntryTools(
    McpCallContext call,
    IdempotentUnit idempotency,
    IFlockLookup flocks,
    IEggOperationsModule eggs,
    IValidator<RecordDailyEntryCommand> validator)
{
    public const string RecordDailyEntry = "cluckwork_record_daily_entry";
    public const string SubmitDailyEntry = "cluckwork_submit_daily_entry";

    [McpServerTool(Name = RecordDailyEntry, Idempotent = true, OpenWorld = false)]
    [Description("Record a flock's egg production for one day as a draft daily entry. Recording again for the "
        + "same flock and date replaces the draft's numbers; leave grades out to keep its grade lines. A submitted "
        + "entry cannot be changed here. Until the draft is submitted, by a person in Cluckwork or with "
        + SubmitDailyEntry + ", its eggs are not in stock.")]
    [Authorize(Policy = AuthPolicies.ProductionWrite)]
    [Authorize(Policy = AuthPolicies.DailyEntriesWriteScope)]
    [MirrorsRoute("RecordDailyEntry")]
    public async Task<RecordedDailyEntry> RecordAsync(
        [Description(McpWrite.KeyDescription + "Before retrying an older call, check the current entry with "
            + "cluckwork_list_daily_entries, or ask a person when this connection cannot read.")]
        string idempotencyKey,
        [Description("The flock's name, ignoring case, or its id when several flocks share the name.")] string flock,
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
        if (string.IsNullOrWhiteSpace(flock))
            throw new McpException("flock is required.");

        return await McpWrite.RunAsync(idempotency, call, RecordDailyEntry, idempotencyKey,
            new { flock, date, totalEggs, crackedEggs, dirtyEggs, discardedEggs, mortalityCount, grades },
            () => RecordOnceAsync(flock, date, totalEggs, crackedEggs, dirtyEggs, discardedEggs, mortalityCount, grades, ct),
            // A Worker unassigned since the first call gets the refusal a fresh call gets.
            async reply => await flocks.GetAsync(reply.FlockId, ct) is null
                ? throw FlockTools.NotFound(flock)
                : reply with { Replayed = true },
            ct);
    }

    [McpServerTool(Name = SubmitDailyEntry, Idempotent = true, OpenWorld = false)]
    [Description("Submit a draft daily entry, as a person does with Submit day in Cluckwork: the entry is locked and "
        + "its sellable eggs become egg lots in stock. Name the entry by the id cluckwork_record_daily_entry returned, "
        + "or by flock and date. An entry that is already submitted is refused. Only an Admin or Manager can correct a "
        + "submitted entry, in Cluckwork.")]
    [Authorize(Policy = AuthPolicies.ProductionWrite)]
    [Authorize(Policy = AuthPolicies.DailyEntriesSubmitScope)]
    [MirrorsRoute("SubmitDailyEntry")]
    public async Task<SubmittedDailyEntry> SubmitAsync(
        [Description(McpWrite.KeyDescription + "Before retrying an older call, check the entry's status with "
            + "cluckwork_list_daily_entries, or ask a person when this connection cannot read.")]
        string idempotencyKey,
        [Description("The entry's id. Leave it out to name the entry by flock and date.")] Guid? dailyEntryId = null,
        [Description("The flock's name, ignoring case, or its id. Use with date, instead of dailyEntryId.")]
        string? flock = null,
        [Description("The production day, YYYY-MM-DD. Use with flock.")] DateOnly? date = null,
        CancellationToken ct = default)
    {
        if (dailyEntryId is not null ? flock is not null || date is not null : string.IsNullOrWhiteSpace(flock) || date is null)
            throw new McpException("Name the entry by dailyEntryId alone, or by flock and date.");

        return await McpWrite.RunAsync(idempotency, call, SubmitDailyEntry, idempotencyKey,
            new { dailyEntryId, flock, date },
            () => SubmitOnceAsync(dailyEntryId, flock, date, ct),
            async reply => await flocks.GetAsync(reply.FlockId, ct) is null
                ? throw FlockTools.NotFound(flock ?? reply.FlockId.ToString())
                : reply with { Replayed = true },
            ct);
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
        if (await McpWrite.ValidateAsync(validator, command, "DailyEntry.Invalid", ct) is { IsFailure: true } invalid)
            return Result.Failure<RecordedDailyEntry>(invalid.Error);

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
            var name = line?.Grade?.Trim();
            var grade = name is null ? null : active.FirstOrDefault(g => Guid.TryParse(name, out var gradeId)
                ? g.Id == gradeId
                : string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
            if (grade is null)
                return Result.Failure<List<GradeQuantityDto>>(Error.Validation("DailyEntry.UnknownGrade",
                    $"No active grade '{name}'. Active grades: {string.Join(", ", active.Select(g => g.Name))}."));
            lines.Add(new GradeQuantityDto(grade.Id, line!.Quantity));
        }

        return Result.Success(lines);
    }

    private async Task<Result<SubmittedDailyEntry>> SubmitOnceAsync(
        Guid? dailyEntryId, string? flock, DateOnly? date, CancellationToken ct)
    {
        var entry = dailyEntryId is { } id
            ? await eggs.GetDailyEntryAsync(id, ct)
                ?? throw new McpException($"No daily entry you can see has the id '{id}'.")
            : await FindEntryAsync(flock!, date!.Value, ct);

        var submitted = await eggs.SubmitDailyEntryAsync(entry.Id, call.AccountId, ct);
        if (submitted.IsFailure)
            return Result.Failure<SubmittedDailyEntry>(submitted.Error);

        var name = (await flocks.GetDisplayNamesAsync([entry.FlockId], ct)).GetValueOrDefault(entry.FlockId)?.Name;
        return Result.Success(new SubmittedDailyEntry(
            entry.Id, entry.FlockId, name, entry.Date, submitted.Value.Status, submitted.Value.EggLotIds,
            "Only an Admin or Manager can change it now, with adjust or void on History.", Replayed: false));
    }

    // The record tool writes one entry per flock and day under the flock's own house, so a
    // second match means one was recorded under another house in the app; neither is picked.
    private async Task<DailyEntryDetails> FindEntryAsync(string flock, DateOnly date, CancellationToken ct)
    {
        var flockId = (await FlockTools.ResolveAsync(flocks, flock, ct)).Id;
        return await eggs.ListDailyEntriesAsync(flockId, date, date, 2, 0, ct) switch
        {
            [var only] => only,
            [] => throw new McpException(
                $"Flock '{flock.Trim()}' has no daily entry on {date:yyyy-MM-dd}. Record one with {RecordDailyEntry} first."),
            var many => throw new McpException(
                $"Flock '{flock.Trim()}' has more than one daily entry on {date:yyyy-MM-dd}. Call again with dailyEntryId "
                + $"set to one of these ids: {string.Join(", ", many.Select(e => e.Id))}."),
        };
    }
}

public sealed record GradeLine(
    [property: Description("The grade's name or id.")] string Grade,
    int Quantity);

public sealed record RecordedDailyEntry(Guid DailyEntryId, Guid FlockId, string FlockName, DateOnly Date, bool Replayed);

public sealed record SubmittedDailyEntry(
    Guid DailyEntryId, Guid FlockId, string? FlockName, DateOnly Date, string Status, IReadOnlyList<Guid> EggLotIds,
    string CorrectInApp, bool Replayed);

public sealed record DailyEntryRow(
    Guid DailyEntryId, Guid FlockId, string? FlockName, DateOnly Date, string Status,
    int TotalEggs, int CrackedEggs, int DirtyEggs, int DiscardedEggs, int MortalityCount,
    IReadOnlyList<GradeCount> Grades);

public sealed record GradeCount(string? Grade, int Quantity);
