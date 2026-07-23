namespace Cluckwork.Application.Tests.Common;

using Cluckwork.Application.Common;

// A frozen farm-local "today" for the date-boundary rules (#35 / #155). Shared
// so validator tests pin an explicit date instead of riding on the build
// machine's clock — which also makes "today", "tomorrow" and "yesterday" mean
// the same thing in every test that uses it.
public sealed class FixedFarmClock(DateOnly today) : IFarmClock
{
    public static readonly DateOnly Today = new(2026, 7, 15);

    public static FixedFarmClock AtDefault() => new(Today);

    public Task<DateOnly> TodayAsync(CancellationToken ct = default) => Task.FromResult(today);
}
