namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Time;
using Microsoft.Extensions.Logging.Abstractions;

// #35. Plain unit tests (no container): FarmClock is the single date boundary
// the stock read, the FIFO allocation and the future-date validators all share,
// so its edges are worth pinning directly.
public sealed class FarmClockTests
{
    // Mirrors SystemClock, but frozen — including the real TimeZoneInfo lookup,
    // so an unusable id throws here exactly as it would in production.
    private sealed class FrozenClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow => utcNow;
        public DateOnly TodayUtc => DateOnly.FromDateTime(utcNow);
        public DateOnly TodayInZone(string timeZoneId)
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz));
        }
    }

    private sealed class StubAccounts(Account? account) : IAccountRepository
    {
        public int Calls { get; private set; }

        public Task<Account?> GetCurrentAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(account);
        }
    }

    private static Account AccountIn(string timeZoneId) =>
        Account.Create(Guid.NewGuid(), "Test farm", timeZoneId, "USD");

    private static FarmClock Build(IAccountRepository accounts, IClock clock) =>
        new(accounts, clock, NullLogger<FarmClock>.Instance);

    [Fact]
    public async Task Today_IsTheFarmsDate_NotUtcs()
    {
        // The issue's example: 18:00 on July 15 in Los Angeles is already
        // July 16 in UTC, so a lot restricted through the 15th would read as
        // available a day early on the UTC boundary.
        var clock = new FrozenClock(new DateTime(2026, 7, 16, 1, 0, 0, DateTimeKind.Utc));
        var farmClock = Build(new StubAccounts(AccountIn("America/Los_Angeles")), clock);

        Assert.Equal(new DateOnly(2026, 7, 16), clock.TodayUtc);
        Assert.Equal(new DateOnly(2026, 7, 15), await farmClock.TodayAsync());
    }

    [Fact]
    public async Task Today_AheadOfUtc_CanBeTomorrowInUtcTerms()
    {
        // The mirror case the old +1-day validator slack existed for: a farm
        // ahead of UTC is legitimately already on the next day.
        var clock = new FrozenClock(new DateTime(2026, 7, 15, 22, 0, 0, DateTimeKind.Utc));
        var farmClock = Build(new StubAccounts(AccountIn("Pacific/Auckland")), clock);

        Assert.Equal(new DateOnly(2026, 7, 15), clock.TodayUtc);
        Assert.Equal(new DateOnly(2026, 7, 16), await farmClock.TodayAsync());
    }

    // The three "cannot resolve the boundary" cases all FAIL CLOSED. Falling
    // back to UTC would be failing open on a medication-safety rule: UTC is the
    // exact wrong answer this class exists to stop, so it would release a lot
    // restricted through today precisely when the farm is misconfigured.

    [Fact]
    public async Task NoAccount_Throws_RatherThanAssumingUtc()
    {
        var clock = new FrozenClock(new DateTime(2026, 7, 16, 1, 0, 0, DateTimeKind.Utc));
        var farmClock = Build(new StubAccounts(null), clock);

        await Assert.ThrowsAsync<FarmTimeZoneException>(() => farmClock.TodayAsync());
    }

    [Fact]
    public async Task UnusableTimeZone_Throws_RatherThanAssumingUtc()
    {
        var clock = new FrozenClock(new DateTime(2026, 7, 16, 1, 0, 0, DateTimeKind.Utc));
        var farmClock = Build(new StubAccounts(AccountIn("Not/AZone")), clock);

        await Assert.ThrowsAsync<FarmTimeZoneException>(() => farmClock.TodayAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankTimeZone_Throws_RatherThanAssumingUtc(string blank)
    {
        // FindSystemTimeZoneById throws ArgumentException — not
        // TimeZoneNotFoundException — for a blank id, so this case would escape
        // a filter that only caught the timezone-specific exceptions.
        var clock = new FrozenClock(new DateTime(2026, 7, 16, 1, 0, 0, DateTimeKind.Utc));
        var farmClock = Build(new StubAccounts(AccountIn(blank)), clock);

        await Assert.ThrowsAsync<FarmTimeZoneException>(() => farmClock.TodayAsync());
    }

    [Fact]
    public async Task AccountIsReadOnce_SoOneRequestSharesOneBoundary()
    {
        // The stock read and the allocation in the same request must agree —
        // and must not each pay for an account round-trip.
        var accounts = new StubAccounts(AccountIn("America/Los_Angeles"));
        var farmClock = Build(accounts, new FrozenClock(new DateTime(2026, 7, 16, 1, 0, 0, DateTimeKind.Utc)));

        var first = await farmClock.TodayAsync();
        var second = await farmClock.TodayAsync();

        Assert.Equal(first, second);
        Assert.Equal(1, accounts.Calls);
    }
}
