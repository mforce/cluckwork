namespace Cluckwork.Application.Features.Flocks;

// #852: Flock Management's write port for daily-entry mortality. AppendAsync
// adds one bird-movement row to the caller's unit of work and never saves, so
// the row commits or rolls back with the caller's own changes.
public interface IMortalityLedger
{
    // Positive birdsRemoved records Mortality; negative records an Adjustment
    // that puts birds back. A note over the ledger's limit is truncated.
    Task AppendAsync(
        Guid accountId, Guid flockId, DateOnly date, int birdsRemoved, Guid dailyEntryId, string note,
        CancellationToken ct);
}
