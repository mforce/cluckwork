namespace Cluckwork.Application.Features.Inventory.UpdateWaterUsage;

// Flock and date are fixed (identity-like); everything else is correctable.
public sealed record UpdateWaterUsageCommand(
    Guid WaterUsageId,
    decimal? Quantity,
    string? Unit,
    string Source,
    decimal? MeterStart,
    decimal? MeterEnd,
    string? Note);
