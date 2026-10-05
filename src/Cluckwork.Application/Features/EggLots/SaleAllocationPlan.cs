using Cluckwork.Domain.Eggs;

namespace Cluckwork.Application.Features.EggLots;

// #612 — whether the plan covered the whole order, and — when it did not —
// which grade ran short and by how much. The CALLER turns that into an Error:
// the grade name lookup is async, and whether the message may name the grade
// or amount at all depends on caller privacy, which this pure planner does
// not decide.
[ModuleContract("EggOperations")]
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
