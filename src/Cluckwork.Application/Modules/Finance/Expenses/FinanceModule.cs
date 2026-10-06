using Cluckwork.Application.Modules.Finance.Contracts;
using Cluckwork.Application.Modules.Finance.Expenses.AdjustExpense;
using Cluckwork.Application.Modules.Finance.Expenses.CreateExpense;
using Cluckwork.Application.Modules.Finance.Expenses.CreateExpenseCategory;
using Cluckwork.Application.Modules.Finance.Expenses.UpdateExpenseCategory;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Modules.Farm.Accounts;
using Cluckwork.Domain.Modules.Finance.Expenses;

namespace Cluckwork.Application.Modules.Finance.Expenses;

public sealed class FinanceModule(
    IExpenseCategoryRepository categories,
    IExpenseRepository expenses,
    CreateExpenseCategoryHandler createCategory,
    UpdateExpenseCategoryHandler updateCategory,
    CreateExpenseHandler createExpense,
    AdjustExpenseHandler adjustExpense) : IFinanceModule
{
    public async Task<IReadOnlyList<ExpenseCategoryDetails>> ListCategoriesAsync(bool includeInactive, CancellationToken ct)
    {
        var list = includeInactive
            ? await categories.ListAllAsync(ct)
            : await categories.ListActiveAsync(SeedDefaults.FarmId, ct);
        return list.Select(c => new ExpenseCategoryDetails(c.Id, c.FarmId, c.Name, c.Active)).ToList();
    }

    public Task<Result<Guid>> CreateCategoryAsync(CreateExpenseCategoryCommand command, Guid accountId, CancellationToken ct) =>
        createCategory.HandleAsync(command, accountId, ct);

    public Task<Result> UpdateCategoryAsync(UpdateExpenseCategoryCommand command, CancellationToken ct) =>
        updateCategory.HandleAsync(command, ct);

    public async Task<ExpenseListPage> ListExpensesAsync(
        DateOnly? from, DateOnly? to, Guid? categoryId, int limit, int offset, CancellationToken ct)
    {
        var list = await expenses.ListAsync(from, to, categoryId, limit, offset, ct);
        var total = await expenses.SumAsync(from, to, categoryId, ct);
        return new ExpenseListPage(list.Select(ToDetails).ToList(), total);
    }

    public async Task<ExpenseDetails?> GetExpenseAsync(Guid id, CancellationToken ct) =>
        await expenses.GetByIdAsync(id, ct) is { } expense ? ToDetails(expense) : null;

    public Task<Result<Guid>> CreateExpenseAsync(CreateExpenseCommand command, Guid accountId, CancellationToken ct) =>
        createExpense.HandleAsync(command, accountId, ct);

    public Task<Result> AdjustExpenseAsync(AdjustExpenseCommand command, CancellationToken ct) =>
        adjustExpense.HandleAsync(command, ct);

    private static ExpenseDetails ToDetails(Expense e) =>
        new(e.Id, e.FarmId, e.ExpenseCategoryId, e.Date, e.Description,
            e.AmountMinorUnits, e.CurrencyCode, e.CurrencyMinorUnit,
            e.FlockId, e.Note, e.Version);
}
