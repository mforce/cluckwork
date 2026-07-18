namespace Cluckwork.Application.Features.Inventory.RecordFeedUsage;

public sealed record RecordFeedUsageCommand(
    Guid FlockId,
    Guid InventoryItemId,
    DateOnly Date,
    decimal Quantity,
    string? Note);

// CurrencyMinorUnit included so clients render non-2-decimal currencies (JPY,
// KWD) correctly, matching the GET shape.
public sealed record RecordFeedUsageResponse(
    Guid FeedUsageId, decimal QuantityUsed, long EstimatedCostMinorUnits,
    string CurrencyCode, int CurrencyMinorUnit);
