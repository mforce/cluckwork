namespace Cluckwork.Application.Modules.FlockManagement.Contracts;

// Manual ledger entry: culls and corrections. Mortality rows are generated
// from submitted daily entries only — a manual Mortality type would double
// count once the day is submitted.
[ModuleContract("FlockManagement")]
public sealed record RecordBirdMovementCommand(
    Guid FlockId,
    DateOnly Date,
    string Type,
    int Quantity,
    string? Note);
