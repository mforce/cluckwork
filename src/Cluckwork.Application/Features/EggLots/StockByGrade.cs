using Cluckwork.Domain.Eggs;

namespace Cluckwork.Application.Features.EggLots;

[ModuleContract("EggOperations")]
public sealed record StockByGrade(
    Guid EggGradeId, string GradeName, int SortOrder, int Available, int Restricted,
    // #911 — the grade's low-stock floor in eggs, null when it has none.
    // BelowFloor measures AVAILABLE against it and never counts Restricted:
    // restricted eggs exist but cannot be sold, so counting them would hide a
    // grade that has nothing sellable left.
    int? LowStockFloor)
{
    public bool BelowFloor => LowStockFloor is int floor && Available < floor;
}
