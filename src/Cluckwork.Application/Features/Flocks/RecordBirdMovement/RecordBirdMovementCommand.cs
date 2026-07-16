namespace Cluckwork.Application.Features.Flocks.RecordBirdMovement;

// Manual ledger entry: culls and corrections. Mortality rows are generated
// from submitted daily entries only — a manual Mortality type would double
// count once the day is submitted.
public sealed record RecordBirdMovementCommand(
    Guid FlockId,
    DateOnly Date,
    string Type,
    int Quantity,
    string? Note);
