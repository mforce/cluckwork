using Cluckwork.Application.Modules.Finance.Contracts;
using Cluckwork.Application.Modules.Finance.Expenses;
using Cluckwork.Application.Modules.Finance.Expenses.AdjustExpense;
using Cluckwork.Application.Modules.Finance.Expenses.CreateExpense;
using Cluckwork.Application.Modules.Finance.Expenses.CreateExpenseCategory;
using Cluckwork.Application.Modules.Finance.Expenses.UpdateExpenseCategory;
using Cluckwork.Infrastructure.Repositories;
using FluentValidation;

namespace Cluckwork.Api.Modules.Finance;

internal static class FinanceModuleServiceCollectionExtensions
{
    public static IServiceCollection AddFinanceModule(this IServiceCollection services)
    {
        services.AddScoped<IExpenseCategoryRepository, ExpenseCategoryRepository>();
        services.AddScoped<IExpenseRepository, ExpenseRepository>();
        services.AddScoped<
            IValidator<CreateExpenseCategoryCommand>,
            CreateExpenseCategoryValidator>();
        services.AddScoped<
            IValidator<UpdateExpenseCategoryCommand>,
            UpdateExpenseCategoryValidator>();
        services.AddScoped<
            IValidator<CreateExpenseCommand>,
            CreateExpenseValidator>();
        services.AddScoped<
            IValidator<AdjustExpenseCommand>,
            AdjustExpenseValidator>();
        services.AddScoped<CreateExpenseCategoryHandler>();
        services.AddScoped<UpdateExpenseCategoryHandler>();
        services.AddScoped<CreateExpenseHandler>();
        services.AddScoped<AdjustExpenseHandler>();
        services.AddScoped<IFinanceModule, FinanceModule>();

        return services;
    }
}
