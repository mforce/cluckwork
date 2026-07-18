namespace Cluckwork.Application.Features.Inventory.RecordFeedUsage;

public sealed record RecordFeedUsageCommand(
    Guid FlockId,
    Guid InventoryItemId,
    DateOnly Date,
    decimal Quantity,
    string? Note);

public sealed record RecordFeedUsageResponse(
    Guid FeedUsageId, decimal QuantityUsed, long EstimatedCostMinorUnits, string CurrencyCode);
