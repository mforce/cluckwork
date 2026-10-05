namespace Cluckwork.Application.Features.Expenses;

// Simulation fixture reads; registered beside the seeders outside Production.
[ModuleContract("Finance")]
public interface IFinanceFixture
{
    Task<Guid?> FindCategoryIdByNameAsync(string name, CancellationToken ct = default);

    Task<bool> ExpenseExistsAsync(string description, CancellationToken ct = default);

    Task<FinanceFixtureCounts> CountAsync(CancellationToken ct = default);
}

[ModuleContract("Finance")]
public sealed record FinanceFixtureCounts(int ExpenseCategories, int Expenses);
