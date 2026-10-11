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
        var (take, skip) = McpPage.Window(limit, offset);
        var rows = await eggs.ListDailyEntriesAsync(flockId, from, to, take + 1, skip, ct);
        var names = await flocks.GetDisplayNamesAsync([.. rows.Select(e => e.FlockId).Distinct()], ct);
        var grades = (await eggs.ListAllGradesAsync(ct)).ToDictionary(g => g.Id, g => g.Name);
        return McpPage.From(rows, take, skip, e => new DailyEntryRow(
            e.Id, e.FlockId, names.GetValueOrDefault(e.FlockId)?.Name, e.Date, e.Status.ToString(),
            e.TotalEggs, e.CrackedEggs, e.DirtyEggs, e.DiscardedEggs, e.MortalityCount,
            [.. e.Grades.Select(g => new GradeCount(grades.GetValueOrDefault(g.EggGradeId), g.Quantity))]));
    }
}

public sealed record DailyEntryRow(
    Guid DailyEntryId, Guid FlockId, string? FlockName, DateOnly Date, string Status,
    int TotalEggs, int CrackedEggs, int DirtyEggs, int DiscardedEggs, int MortalityCount,
    IReadOnlyList<GradeCount> Grades);

public sealed record GradeCount(string? Grade, int Quantity);
