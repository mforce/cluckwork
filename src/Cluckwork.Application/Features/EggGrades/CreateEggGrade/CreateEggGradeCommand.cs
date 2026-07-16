namespace Cluckwork.Application.Features.EggGrades.CreateEggGrade;

public sealed record CreateEggGradeCommand(
    string Name,
    string GradeType,
    int SortOrder,
    bool IsSaleable);
