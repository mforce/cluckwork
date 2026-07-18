namespace Cluckwork.Application.Features.Inventory.RecordWaterUsage;

// Quantity may be omitted when meters are given (derived as end − start);
// when both are present they must agree. Unit defaults to liters.
public sealed record RecordWaterUsageCommand(
    Guid FlockId,
    DateOnly Date,
    decimal? Quantity,
    string? Unit,
    string Source,
    decimal? MeterStart,
    decimal? MeterEnd,
    string? Note);
