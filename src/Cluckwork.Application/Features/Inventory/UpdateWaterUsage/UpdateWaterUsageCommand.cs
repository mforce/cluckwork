namespace Cluckwork.Application.Features.Inventory.UpdateWaterUsage;

// Flock and date are fixed (identity-like); everything else is correctable.
// Version is the client's base version (from the list response): a stale form
// gets a 409 instead of silently overwriting an intervening edit.
public sealed record UpdateWaterUsageCommand(
    Guid WaterUsageId,
    int Version,
    decimal? Quantity,
    string? Unit,
    string Source,
    decimal? MeterStart,
    decimal? MeterEnd,
    string? Note);
