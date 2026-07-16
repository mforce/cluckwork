namespace Cluckwork.Application.Features.EggGrades.UpdateEggGrade;

public sealed record UpdateEggGradeCommand(
    Guid EggGradeId,
    string Name,
    int SortOrder,
    bool IsSaleable);
