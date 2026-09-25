namespace Cluckwork.Application.Features.EggGrades.UpdateEggGrade;

public sealed record UpdateEggGradeCommand(
    Guid EggGradeId,
    string Name,
    int SortOrder,
    bool IsSaleable,
    // #911 — the floor as it should stand after this update; null clears it.
    int? LowStockFloor = null);
