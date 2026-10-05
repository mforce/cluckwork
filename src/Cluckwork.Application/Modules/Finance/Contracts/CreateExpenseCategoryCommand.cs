namespace Cluckwork.Application.Modules.Finance.Contracts;

[ModuleContract("Finance")]
public sealed record CreateExpenseCategoryCommand(string Name);
