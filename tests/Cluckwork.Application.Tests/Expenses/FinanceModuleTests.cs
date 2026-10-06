using Cluckwork.Application.Modules.Finance.Contracts;
using Cluckwork.Application.Modules.Finance.Expenses;
using Cluckwork.Domain.Modules.Farm.Accounts;
using Cluckwork.Domain.Modules.Finance.Expenses;

namespace Cluckwork.Application.Tests.Expenses;

// #849: the read paths copy the aggregate field by field into positional
// records, where two Guids or two ints can swap without a compile error.
public sealed class FinanceModuleTests
{
    private static readonly Guid ExpenseId = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid AccountId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid FarmId = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly Guid CategoryId = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid FlockId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    private static readonly ExpenseDetails Expected = new(
        ExpenseId, FarmId, CategoryId, new DateOnly(2026, 9, 30), "Feed delivery",
        12_345, "KWD", 3, FlockId, "north barn", 0);

    private readonly FakeExpenses _expenses = new();
    private readonly FakeCategories _categories = new();

    // The handlers sit on the write paths only; these tests exercise the reads.
    private FinanceModule Module() => new(_categories, _expenses, null!, null!, null!, null!);

    public FinanceModuleTests() => _expenses.Rows.Add(Expense.Create(
        ExpenseId, AccountId, FarmId, CategoryId, new DateOnly(2026, 9, 30), "Feed delivery",
        12_345, "KWD", 3, FlockId, "north barn"));

    [Fact]
    public async Task GetExpense_CopiesEveryField()
    {
        Assert.Equal(Expected, await Module().GetExpenseAsync(ExpenseId, default));
        Assert.Null(await Module().GetExpenseAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task ListExpenses_ForwardsTheFiltersAndReturnsThePageWithItsTotal()
    {
        var page = await Module().ListExpensesAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), CategoryId, 25, 50, default);

        Assert.Equal([Expected], page.Items);
        Assert.Equal(99_999, page.TotalMinorUnits);
        Assert.Equal(
            "list 2026-09-01..2026-09-30 00000000-0000-0000-0000-0000000000c1 25/50; sum 2026-09-01..2026-09-30 00000000-0000-0000-0000-0000000000c1",
            string.Join("; ", _expenses.Calls));
    }

    [Fact]
    public async Task ListCategories_ActiveReadsTheSeededFarm_InactiveReadsAll()
    {
        Assert.Equal([new ExpenseCategoryDetails(CategoryId, FarmId, "Feed", true)],
            await Module().ListCategoriesAsync(includeInactive: false, default));
        Assert.Equal([new ExpenseCategoryDetails(CategoryId, FarmId, "Feed", false)],
            await Module().ListCategoriesAsync(includeInactive: true, default));
        Assert.Equal([$"active {SeedDefaults.FarmId}", "all"], _categories.Calls);
    }

    private sealed class FakeExpenses : IExpenseRepository
    {
        public List<Expense> Rows { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<Expense?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Rows.SingleOrDefault(e => e.Id == id));

        public Task<IReadOnlyList<Expense>> ListAsync(
            DateOnly? from, DateOnly? to, Guid? categoryId, int limit, int offset, CancellationToken ct = default)
        {
            Calls.Add($"list {from:yyyy-MM-dd}..{to:yyyy-MM-dd} {categoryId} {limit}/{offset}");
            return Task.FromResult<IReadOnlyList<Expense>>(Rows);
        }

        public Task<long> SumAsync(DateOnly? from, DateOnly? to, Guid? categoryId, CancellationToken ct = default)
        {
            Calls.Add($"sum {from:yyyy-MM-dd}..{to:yyyy-MM-dd} {categoryId}");
            return Task.FromResult(99_999L);
        }

        public Task AddAsync(Expense entity, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeCategories : IExpenseCategoryRepository
    {
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<ExpenseCategory>> ListActiveAsync(Guid farmId, CancellationToken ct = default)
        {
            Calls.Add($"active {farmId}");
            return Task.FromResult<IReadOnlyList<ExpenseCategory>>([ExpenseCategory.Create(CategoryId, AccountId, FarmId, "Feed")]);
        }

        public Task<IReadOnlyList<ExpenseCategory>> ListAllAsync(CancellationToken ct = default)
        {
            Calls.Add("all");
            var inactive = ExpenseCategory.Create(CategoryId, AccountId, FarmId, "Feed");
            inactive.Deactivate();
            return Task.FromResult<IReadOnlyList<ExpenseCategory>>([inactive]);
        }

        public Task<bool> NameExistsAsync(Guid farmId, string name, Guid? excludeId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ExpenseCategory?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddAsync(ExpenseCategory entity, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
