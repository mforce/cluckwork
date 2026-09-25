namespace Cluckwork.Application.Features.EggGrades.CreateEggGrade;

public sealed record CreateEggGradeCommand(
    string Name,
    string GradeType,
    int SortOrder,
    bool IsSaleable,
    // #911 — null means the grade raises no low-stock warning.
    int? LowStockFloor = null);
