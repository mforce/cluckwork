using System.ComponentModel;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.EggOperations.Contracts;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cluckwork.Api.Mcp;

// #807 — egg stock and the lot ledger behind it. A movement's reason is farm-entered text
// and is left out (#792).
[McpServerToolType]
public sealed class StockTools
{
    private readonly IEggOperationsModule eggs;
    private readonly IFarmClock farmClock;

    // Constructing McpCallContext is the identity check (#805); nothing here reads it.
    public StockTools(McpCallContext _, IEggOperationsModule eggs, IFarmClock farmClock)
    {
        this.eggs = eggs;
        this.farmClock = farmClock;
    }

    // ponytail: the contract returns the whole grade catalogue and this pages it in memory.
    // A catalogue is a handful of grades; page it in the query if farms ever hold hundreds.
    [McpServerTool(Name = "cluckwork_read_stock", ReadOnly = true)]
    [Authorize]
    [Authorize(Policy = AuthPolicies.FarmReadScope)]
    [MirrorsRoute("GetStock")]
    [Description("Eggs in stock today, per grade. Available excludes withdrawal-restricted eggs, which are counted "
        + "separately; belowFloor is true when available is under the grade's low-stock floor. "
        + "Paged: when complete is false, call again with offset set to nextOffset.")]
    public async Task<McpPage<StockRow>> ReadStockAsync(
        [Description("Grades per page, 1 to 100.")] int limit = McpPage.DefaultLimit,
        int offset = 0,
        CancellationToken ct = default)
    {
        var (take, skip) = McpPage.Window(limit, offset);
        var rows = await eggs.GetStockByGradeAsync(await farmClock.TodayAsync(ct), ct);
        return McpPage.From([.. rows.Skip(skip).Take(take + 1)], take, skip, r => new StockRow(
            r.GradeName, r.Available, r.Restricted, r.LowStockFloor, r.BelowFloor));
    }

    [McpServerTool(Name = "cluckwork_list_egg_lots", ReadOnly = true)]
    [Authorize]
    [Authorize(Policy = AuthPolicies.FarmReadScope)]
    [MirrorsRoute("ListEggLots")]
    [Description("Egg lots, newest production first: how many eggs each produced and how many are still available. "
        + "Paged: when complete is false, call again with offset set to nextOffset.")]
    public async Task<McpPage<EggLotRow>> ListEggLotsAsync(
        [Description("Only lots of this grade, by name or id.")] string? grade = null,
        [Description("Earliest production date, yyyy-mm-dd.")] DateOnly? from = null,
        [Description("Latest production date, yyyy-mm-dd.")] DateOnly? to = null,
        [Description("Lots per page, 1 to 100.")] int limit = McpPage.DefaultLimit,
        int offset = 0,
        CancellationToken ct = default)
    {
        var grades = await eggs.ListAllGradesAsync(ct);
        var gradeId = grade is null ? (Guid?)null : ResolveGrade(grades, grade);
        var names = grades.ToDictionary(g => g.Id, g => g.Name);
        var (take, skip) = McpPage.Window(limit, offset);
        var rows = await eggs.ListLotsAsync(gradeId, from, to, take + 1, skip, ct);
        return McpPage.From(rows, take, skip, l => new EggLotRow(
            l.Id, names.GetValueOrDefault(l.EggGradeId), l.ProductionDate, l.QuantityProduced,
            l.QuantityAvailable, l.RestrictedUntil, l.DailyEntryId));
    }

    // ponytail: the contract returns one lot's whole ledger and this pages it in memory. A
    // lot's ledger is its production plus each draw against it; add paging to
    // ListLotMovementsAsync if lots ever carry thousands of movements.
    [McpServerTool(Name = "cluckwork_list_egg_lot_movements", ReadOnly = true)]
    [Authorize]
    [Authorize(Policy = AuthPolicies.FarmReadScope)]
    [MirrorsRoute("ListEggLotMovements")]
    [Description("One egg lot's movements, newest first: every change to its available quantity and what caused it. "
        + "Paged: when complete is false, call again with offset set to nextOffset.")]
    public async Task<McpPage<EggLotMovementRow>> ListEggLotMovementsAsync(
        [Description("The lot's id, from cluckwork_list_egg_lots.")] Guid lotId,
        [Description("Movements per page, 1 to 100.")] int limit = McpPage.DefaultLimit,
        int offset = 0,
        CancellationToken ct = default)
    {
        var (take, skip) = McpPage.Window(limit, offset);
        var rows = await eggs.ListLotMovementsAsync(lotId, ct)
            ?? throw new McpException($"No egg lot {lotId} exists on this farm.");
        return McpPage.From([.. rows.Skip(skip).Take(take + 1)], take, skip, m => new EggLotMovementRow(
            m.MovementType.ToString(), m.QuantityDelta, m.ReferenceType, m.ReferenceId, m.CreatedAtUtc));
    }

    // Grade names are unique per farm, ignoring case, but an account's catalogue spans its farms.
    private static Guid ResolveGrade(IReadOnlyList<EggGradeDetails> grades, string grade)
    {
        var text = grade.Trim();
        var matches = Guid.TryParse(text, out var id)
            ? grades.Where(g => g.Id == id).ToList()
            : grades.Where(g => string.Equals(g.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches switch
        {
            [var only] => only.Id,
            [] => throw new McpException($"No egg grade is named or numbered '{text}'."),
            _ => throw new McpException(
                $"More than one egg grade is named '{text}'. Call again with grade set to one of these ids: "
                + string.Join(", ", matches.Select(g => g.Id)) + "."),
        };
    }
}

public sealed record StockRow(string Grade, int Available, int Restricted, int? LowStockFloor, bool BelowFloor);

public sealed record EggLotRow(
    Guid LotId, string? Grade, DateOnly ProductionDate, int QuantityProduced, int QuantityAvailable,
    DateOnly? RestrictedUntil, Guid? DailyEntryId);

public sealed record EggLotMovementRow(
    string MovementType, int QuantityDelta, string ReferenceType, Guid ReferenceId, DateTimeOffset RecordedAtUtc);
