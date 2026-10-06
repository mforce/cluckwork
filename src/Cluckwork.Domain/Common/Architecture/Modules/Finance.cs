namespace Cluckwork.Domain.Common.Architecture;

[ModuleOwner("Finance", "module",
    Namespaces = [
        "Cluckwork.Domain.Modules.Finance",
        "Cluckwork.Application.Modules.Finance",
        "Cluckwork.Infrastructure.Modules.Finance",
    ],
    Implementations = [
        "Cluckwork.Infrastructure.Modules.Finance.Repositories.ExpenseCategoryRepository",
        "Cluckwork.Infrastructure.Modules.Finance.Repositories.ExpenseRepository",
        "Cluckwork.Infrastructure.Modules.Finance.Repositories.FinanceFixture",
    ])]
[ModuleEdge(
    "Finance", "Farm", "R",
    "CreateExpenseHandler injects Farm's IAccountRepository for the lock-aware currency snapshot an expense binds to (#162, FOR SHARE on the account row), and it and CreateExpenseCategoryHandler attach their rows to Domain.Accounts.SeedDefaults.FarmId, whose active categories FinanceModule lists. Design 3.4 classes this cell W, but the code reads Farm state under a shared row lock and mutates nothing there, the same shape 3.4 classes R on Commerce -> Farm and Inventory -> Farm; the ledger records what the code does and notes the disagreement here so #848 regenerates the matrix from this reading.",
    "Cluckwork.Application.Modules.Finance.Expenses.CreateExpense.CreateExpenseHandler",
    "Cluckwork.Application.Modules.Finance.Expenses.CreateExpenseCategory.CreateExpenseCategoryHandler",
    "Cluckwork.Application.Modules.Finance.Expenses.FinanceModule")]
[ModuleEdge(
    "Finance", "FlockManagement", "R",
    "CreateExpenseHandler and AdjustExpenseHandler inject Flock Management's IFlockLookup port to validate the optional flock an expense is attributed to. Design 3.4 row Finance -> Flock = R.",
    "Cluckwork.Application.Modules.Finance.Expenses.AdjustExpense.AdjustExpenseHandler",
    "Cluckwork.Application.Modules.Finance.Expenses.CreateExpense.CreateExpenseHandler")]
internal static class FinanceModuleRules { }
