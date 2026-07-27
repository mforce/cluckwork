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
        // currency change never re-denominates recorded expenses. The snapshot
        // and the insert share a transaction, with FOR SHARE on the account
        // row (#162): a concurrent currency change either waits for this
        // commit (and then refuses — the probe sees this row) or holds its
        // FOR UPDATE first, in which case this read waits and stamps the NEW
        // currency. Never a row in one denomination on a farm in another.
        Result<Guid>? outcome = null;
        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            var account = await accounts.GetCurrentSharedLockedAsync(transactionCt);
            if (account is null)
            {
                outcome = Result.Failure<Guid>(Error.NotFound("Account", accountId));
                return false;
            }

            var expense = Expense.Create(
                Guid.NewGuid(), accountId, farmId, command.ExpenseCategoryId,
                command.Date, command.Description, command.AmountMinorUnits,
                account.DefaultCurrencyCode, account.DefaultCurrencyMinorUnit,
                command.FlockId, command.Note);

            await expenses.AddAsync(expense, transactionCt);
            outcome = Result.Success(expense.Id);
            return true;
        }, ct);

        return outcome!;
    }
}
