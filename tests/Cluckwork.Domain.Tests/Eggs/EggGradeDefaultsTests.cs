namespace Cluckwork.Domain.Tests.Eggs;

using Cluckwork.Domain.Eggs;

public sealed class EggGradeDefaultsTests
{
    [Fact]
    public void Defaults_ReturnsTheTenCanonicalGrades()
    {
        var accountId = Guid.NewGuid();
        var farmId = Guid.NewGuid();

        var grades = EggGrade.Defaults(accountId, farmId);

        Assert.Collection(grades,
            grade => AssertGrade(grade, "Small", EggGradeType.Size, 0, true, DailyEntryKind.Manual),
            grade => AssertGrade(grade, "Medium", EggGradeType.Size, 1, true, DailyEntryKind.Manual),
            grade => AssertGrade(grade, "Large", EggGradeType.Size, 2, true, DailyEntryKind.Manual),
            grade => AssertGrade(grade, "Jumbo", EggGradeType.Size, 3, true, DailyEntryKind.Manual),
            grade => AssertGrade(grade, "Seconds", EggGradeType.Quality, 4, true, DailyEntryKind.Manual),
            grade => AssertGrade(grade, "Cracked", EggGradeType.Quality, 5, true, DailyEntryKind.Cracked),
            grade => AssertGrade(grade, "Dirty", EggGradeType.Quality, 6, true, DailyEntryKind.Dirty),
            grade => AssertGrade(grade, "Soft Shell", EggGradeType.Quality, 7, false, DailyEntryKind.Manual),
            grade => AssertGrade(grade, "Discarded", EggGradeType.Custom, 8, false, DailyEntryKind.Manual),
            grade => AssertGrade(grade, "Internal Use", EggGradeType.Custom, 9, false, DailyEntryKind.Manual));

        Assert.Equal(grades.Count, grades.Select(grade => grade.Id).Distinct().Count());
        Assert.All(grades, grade =>
        {
            Assert.NotEqual(Guid.Empty, grade.Id);
            Assert.Equal(accountId, grade.AccountId);
            Assert.Equal(farmId, grade.FarmId);
            Assert.True(grade.Active);
            Assert.Equal(0, grade.Version);
        });
    }

    private static void AssertGrade(
        EggGrade grade,
        string name,
        EggGradeType gradeType,
        int sortOrder,
        bool isSaleable,
        DailyEntryKind dailyEntryKind)
    {
        Assert.Equal(name, grade.Name);
        Assert.Equal(gradeType, grade.GradeType);
        Assert.Equal(sortOrder, grade.SortOrder);
        Assert.Equal(isSaleable, grade.IsSaleable);
        Assert.Equal(dailyEntryKind, grade.DailyEntryKind);
    }
}
