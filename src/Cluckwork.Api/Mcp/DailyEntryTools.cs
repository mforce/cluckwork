using System.ComponentModel;
using Cluckwork.Application.Modules.EggOperations.Contracts;
using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace Cluckwork.Api.Mcp;

// #807 — daily production entries. The adjust and void reasons are farm-entered text and
// are left out (#792). Flock scope comes from the request's query filters.
[McpServerToolType]
public sealed class DailyEntryTools
{
    private readonly IEggOperationsModule eggs;
    private readonly IFlockLookup flocks;

    // Constructing McpCallContext is the identity check (#805); nothing here reads it.
    public DailyEntryTools(McpCallContext _, IEggOperationsModule eggs, IFlockLookup flocks)
    {
        this.eggs = eggs;
        this.flocks = flocks;
    }

    [McpServerTool(Name = "cluckwork_list_daily_entries", ReadOnly = true)]
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
}

public sealed record DailyEntryRow(
    Guid DailyEntryId, Guid FlockId, string? FlockName, DateOnly Date, string Status,
    int TotalEggs, int CrackedEggs, int DirtyEggs, int DiscardedEggs, int MortalityCount,
    IReadOnlyList<GradeCount> Grades);

public sealed record GradeCount(string? Grade, int Quantity);
