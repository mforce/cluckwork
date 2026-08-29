namespace Cluckwork.Application.Features.Sales.ConfirmSale;

using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Sales;

public sealed record PlannedEggLotDraw(Guid SalesOrderItemId, Guid EggLotId, int Quantity);

// #612 — whether the plan covered the whole order, and — when it did not —
// which grade ran short and by how much. The CALLER turns that into an Error:
// the grade name lookup is async, and whether the message may name the grade
// or amount at all depends on caller privacy, which this pure planner does
// not decide.
public sealed record SaleAllocationPlan(
    bool IsComplete,
    IReadOnlyList<PlannedEggLotDraw> Draws,
    Guid? ShortEggGradeId,
    int ShortRemaining)
{
    public static SaleAllocationPlan Complete(IReadOnlyList<PlannedEggLotDraw> draws) =>
        new(true, draws, null, 0);

    public static SaleAllocationPlan Short(Guid eggGradeId, int remaining) =>
        new(false, [], eggGradeId, remaining);
}

// #612 — pure whole-order FIFO planner (spec §10.9.1): reads
// EggLot.QuantityAvailable off the given candidate list and never mutates a
// lot or the order. The same immutable input plans identically every time,
// so a caller can try a NARROWER candidate subset first and the SAME full
// locked list second without a second lock or query.
public static class SaleAllocationPlanner
{
    public static SaleAllocationPlan Plan(
        IReadOnlyList<SalesOrderItem> items, IReadOnlyList<EggLot> candidateLots)
    {
        var draws = new List<PlannedEggLotDraw>();
        // A copy, never the lots' own field — repeated grades across items
        // must see each other's draws without mutating the aggregate itself.
        var remainingByLot = candidateLots.ToDictionary(l => l.Id, l => l.QuantityAvailable);

        foreach (var item in items)
        {
            var remaining = item.QuantityBase;
            foreach (var lot in candidateLots.Where(l => l.EggGradeId == item.EggGradeId))
            {
                if (remaining <= 0) break;
                var available = remainingByLot[lot.Id];
                if (available <= 0) continue;
                var take = Math.Min(remaining, available);
                draws.Add(new PlannedEggLotDraw(item.Id, lot.Id, take));
                remainingByLot[lot.Id] = available - take;
                remaining -= take;
            }
            if (remaining > 0)
                return SaleAllocationPlan.Short(item.EggGradeId, remaining);
        }
        return SaleAllocationPlan.Complete(draws);
    }
}
