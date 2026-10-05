using Cluckwork.Application.Modules.EggOperations.Contracts;
using Cluckwork.Domain.Modules.EggOperations.Eggs;

namespace Cluckwork.Application.Modules.EggOperations.EggLots;

// #612 — pure whole-order FIFO planner (spec §10.9.1): reads
// EggLot.QuantityAvailable off the given candidate list and never mutates a
// lot. The same immutable input plans identically every time, so a caller can
// try a NARROWER candidate subset first and the SAME full locked list second
// without a second lock or query.
public static class SaleAllocationPlanner
{
    public static SaleAllocationPlan Plan(
        IReadOnlyList<SaleDemandLine> items, IReadOnlyList<EggLot> candidateLots)
    {
        var draws = new List<PlannedEggLotDraw>();
        // A copy, never the lots' own field — repeated grades across items
        // must see each other's draws without mutating the aggregate itself.
        var remainingByLot = candidateLots.ToDictionary(l => l.Id, l => l.QuantityAvailable);

        foreach (var item in items)
        {
            var remaining = item.Quantity;
            foreach (var lot in candidateLots.Where(l => l.EggGradeId == item.EggGradeId))
            {
                if (remaining <= 0) break;
                var available = remainingByLot[lot.Id];
                if (available <= 0) continue;
                var take = Math.Min(remaining, available);
                draws.Add(new PlannedEggLotDraw(item.LineId, lot.Id, take));
                remainingByLot[lot.Id] = available - take;
                remaining -= take;
            }
            if (remaining > 0)
                return SaleAllocationPlan.Short(item.EggGradeId, remaining);
        }
        return SaleAllocationPlan.Complete(draws);
    }
}
