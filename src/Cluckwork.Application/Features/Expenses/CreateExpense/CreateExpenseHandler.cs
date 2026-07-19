namespace Cluckwork.Application.Features.Expenses.CreateExpense;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Expenses;

public sealed class CreateExpenseHandler(
    IExpenseRepository expenses,
    IExpenseCategoryRepository categories,
    IFlockRepository flocks,
    IAccountRepository accounts,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        CreateExpenseCommand command, Guid accountId, CancellationToken ct)
    {
        var farmId = SeedDefaults.FarmId;

        // New expenses take ACTIVE categories of this farm only; deactivated
        // ones live on inside already-recorded expenses (grade grandfathering).
        var category = await categories.GetByIdAsync(command.ExpenseCategoryId, ct);
        if (category is null || category.FarmId != farmId || !category.Active)
            return Result.Failure<Guid>(Error.Validation(
                "Expense.UnknownCategory",
                "The expense category does not exist or is inactive."));

        if (command.FlockId is { } flockId)
        {
            var flock = await flocks.GetByIdAsync(flockId, ct);
            if (flock is null || flock.FarmId != farmId)
                return Result.Failure<Guid>(Error.Validation(
                    "Expense.UnknownFlock", "The flock does not exist on this farm."));
        }

        // Currency snapshots from the account at creation (spec §16) — a later
        // currency change never re-denominates recorded expenses.
        var account = await accounts.GetCurrentAsync(ct);
        if (account is null)
            return Result.Failure<Guid>(Error.NotFound("Account", accountId));

        var expense = Expense.Create(
            Guid.NewGuid(), accountId, farmId, command.ExpenseCategoryId,
            command.Date, command.Description, command.AmountMinorUnits,
            account.DefaultCurrencyCode, account.DefaultCurrencyMinorUnit,
            command.FlockId, command.Note);

        await expenses.AddAsync(expense, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(expense.Id);
    }
}
