using System.ComponentModel;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cluckwork.Api.Mcp;

// #807 — flock reads. Tenant and flock scope come from the request's query filters, so a
// Worker sees only assigned flocks. Breed is left out with every other farm-entered text
// field (#792).
[McpServerToolType]
public sealed class FlockTools
{
    private readonly IFlockModule flocks;
    private readonly IFarmClock farmClock;

    // Constructing McpCallContext is the identity check (#805); nothing here reads it.
    public FlockTools(McpCallContext _, IFlockModule flocks, IFarmClock farmClock)
    {
        this.flocks = flocks;
        this.farmClock = farmClock;
    }

    [McpServerTool(Name = "cluckwork_list_flocks", ReadOnly = true)]
    [Authorize]
    [Authorize(Policy = AuthPolicies.FarmReadScope)]
    [MirrorsRoute("ListFlocks")]
    [Description("The farm's flocks you can see, by name, with birds alive and whether each accepts production today. "
        + "Paged: when complete is false, call again with offset set to nextOffset.")]
    public async Task<McpPage<FlockRow>> ListFlocksAsync(
        [Description("'active', 'active-and-depleted' (default) or 'all', which adds archived flocks.")]
        string status = "active-and-depleted",
        [Description("Only flocks whose name contains this text, ignoring case.")]
        string? nameContains = null,
        [Description("Flocks per page, 1 to 100.")] int limit = McpPage.DefaultLimit,
        int offset = 0,
        CancellationToken ct = default)
    {
        var eligibility = status switch
        {
            "active" => FlockEligibility.Active,
            "active-and-depleted" => FlockEligibility.ActiveAndDepleted,
            "all" => FlockEligibility.All,
            _ => throw new McpException("status must be 'active', 'active-and-depleted' or 'all'."),
        };
        var (take, skip) = McpPage.Window(limit, offset);
        var rows = await flocks.SearchAsync(nameContains, eligibility, take + 1, skip, ct);
        var removed = await flocks.GetBirdsRemovedAsync([.. rows.Select(f => f.Id)], ct);
        var today = await farmClock.TodayAsync(ct);
        return McpPage.From(rows, take, skip, f => new FlockRow(
            f.Id, f.Name, f.Status.ToString(), f.PlacementDate, f.InitialCount,
            f.InitialCount - removed.GetValueOrDefault(f.Id), f.DepletedOn, f.ArchivedOn,
            f.CanRecordProductionOn(today)));
    }

    // A model holds names, and duplicate flock names are legal, so a name resolves only
    // to exactly one visible flock. Text that parses as an id is read as one.
    internal static async Task<FlockReference> ResolveAsync(IFlockLookup lookup, string flock, CancellationToken ct)
    {
        if (Guid.TryParse(flock, out var id))
            return (await lookup.GetDisplayNamesAsync([id], ct)).GetValueOrDefault(id) ?? throw NotFound(flock);

        return await lookup.ResolveByNameAsync(flock.Trim(), ct) switch
        {
            FlockNameResolution.Found found => found.Flock,
            FlockNameResolution.Ambiguous ambiguous => throw new McpException(
                $"More than one flock is named '{flock.Trim()}'. Call again with flock set to one of these ids: "
                + string.Join(", ", ambiguous.Candidates.Select(c => $"{c.Id} ({c.Status})")) + "."),
            _ => throw NotFound(flock),
        };
    }

    private static McpException NotFound(string flock) => new($"No flock you can see is named or numbered '{flock.Trim()}'.");
}

public sealed record FlockRow(
    Guid FlockId, string Name, string Status, DateOnly PlacementDate, int InitialCount, long BirdsAlive,
    DateOnly? DepletedOn, DateOnly? ArchivedOn, bool AcceptsProductionToday);
