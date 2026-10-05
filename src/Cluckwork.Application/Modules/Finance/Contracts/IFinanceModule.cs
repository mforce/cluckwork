using Cluckwork.Domain.Common;
using Cluckwork.Domain.Modules.Finance.Expenses;

namespace Cluckwork.Application.Modules.Finance.Contracts;

// #849: the Finance module's contract. Adapters reach Finance only through the
// types marked [ModuleContract("Finance")].
[ModuleContract("Finance")]
public interface IFinanceModule
{
    // The EntityType Finance writes on an expense's audit rows; provenance reads key by it.
    const string ExpenseAuditEntityType = nameof(Expense);

    Task<IReadOnlyList<ExpenseCategoryDetails>> ListCategoriesAsync(bool includeInactive, CancellationToken ct);

    Task<Result<Guid>> CreateCategoryAsync(CreateExpenseCategoryCommand command, Guid accountId, CancellationToken ct);

    Task<Result> UpdateCategoryAsync(UpdateExpenseCategoryCommand command, CancellationToken ct);

    Task<ExpenseListPage> ListExpensesAsync(
        DateOnly? from, DateOnly? to, Guid? categoryId, int limit, int offset, CancellationToken ct);

    Task<ExpenseDetails?> GetExpenseAsync(Guid id, CancellationToken ct);

    Task<Result<Guid>> CreateExpenseAsync(CreateExpenseCommand command, Guid accountId, CancellationToken ct);

    Task<Result> AdjustExpenseAsync(AdjustExpenseCommand command, CancellationToken ct);
}

[ModuleContract("Finance")]
public sealed record ExpenseCategoryDetails(Guid Id, Guid FarmId, string Name, bool Active);

[ModuleContract("Finance")]
public sealed record ExpenseDetails(
    Guid Id, Guid FarmId, Guid ExpenseCategoryId, DateOnly Date, string Description,
    long AmountMinorUnits, string CurrencyCode, int CurrencyMinorUnit,
    Guid? FlockId, string? Note, int Version);

[ModuleContract("Finance")]
public sealed record ExpenseListPage(IReadOnlyList<ExpenseDetails> Items, long TotalMinorUnits);
