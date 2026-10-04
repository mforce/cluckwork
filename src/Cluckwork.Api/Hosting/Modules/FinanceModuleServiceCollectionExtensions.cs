using Cluckwork.Application.Features.Expenses;
using Cluckwork.Application.Features.Expenses.AdjustExpense;
using Cluckwork.Application.Features.Expenses.CreateExpense;
using Cluckwork.Application.Features.Expenses.CreateExpenseCategory;
using Cluckwork.Application.Features.Expenses.UpdateExpenseCategory;
using Cluckwork.Infrastructure.Repositories;
using FluentValidation;

namespace Cluckwork.Api.Hosting.Modules;

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
