namespace Cluckwork.Application.Modules.Finance.Contracts;

// Rename and/or flip active — the grade-catalog management shape. Sending the
// current name with a new Active value is a pure activate/deactivate.
[ModuleContract("Finance")]
public sealed record UpdateExpenseCategoryCommand(
    Guid ExpenseCategoryId,
    string Name,
    bool Active);
