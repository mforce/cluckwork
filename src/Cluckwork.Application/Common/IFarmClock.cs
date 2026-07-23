namespace Cluckwork.Application.Common;

// #35 — the farm-local "today" that every date-boundary rule must share: the
// withdrawal-restriction read (stock), FIFO sale allocation, and the
// future-date validators.
//
// They have to agree. Diverging them — some UTC, some farm-local — is worse
// than UTC everywhere, because the same lot can read *available* on one path
// and *restricted* on the other: stock says the eggs are sellable and the
// allocation refuses them, or worse, the reverse. Resolving it in one place is
// the point of this port.
//
// Why not IClock.TodayUtc: for a farm outside UTC that misclassifies around
// midnight. At 18:00 July 15 in America/Los_Angeles, TodayUtc is already
// July 16, so a lot restricted through July 15 — eggs still inside a
// medication withdrawal period — reads as available a day early.
public interface IFarmClock
{
    // Today in the current tenant's Account.TimeZoneId.
    Task<DateOnly> TodayAsync(CancellationToken ct = default);
}
