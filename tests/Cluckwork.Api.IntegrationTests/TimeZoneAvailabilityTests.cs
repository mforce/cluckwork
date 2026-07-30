namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Infrastructure.Time;

// #264 — unit-level coverage of the tz-availability guard (no Docker). Mirrors
// RateLimitingOptionsTests: the guard's LOGIC is proven here; SeedTimeZoneTests
// proves it is WIRED into boot.
public sealed class TimeZoneAvailabilityTests
{
    [Fact]
    public void EnsureResolvable_ValidIanaId_DoesNotThrow()
    {
        var ex = Record.Exception(() => TimeZoneAvailability.EnsureResolvable("Asia/Manila", "test"));
        Assert.Null(ex);
    }

    [Fact]
    public void EnsureResolvable_TheImageCanary_Resolves()
    {
        // Runs in the CI/build environment, which always has tzdata — so it does
        // NOT (and cannot) catch a tzdata-less *base image*; only the Program.cs
        // boot check does that, at real container startup. Its narrower value: it
        // proves the guard accepts a real DST zone, and it fails if THIS
        // environment ever lost tzdata (a regression signal, not the deploy guard).
        var ex = Record.Exception(() =>
            TimeZoneAvailability.EnsureResolvable(TimeZoneAvailability.CanaryZoneId, "canary"));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("Not/AZone")]
    [InlineData("")]
    [InlineData("   ")]
    public void EnsureResolvable_UnusableId_ThrowsWithActionableMessage(string badId)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => TimeZoneAvailability.EnsureResolvable(badId, "ctx"));
        Assert.Contains("ctx", ex.Message);
        Assert.Contains(badId, ex.Message);
        Assert.Contains("tz database", ex.Message);
    }

    [Fact]
    public void EnsureResolvable_NullId_Throws()
    {
        // A null id (e.g. an unset config value reaching the guard) must still
        // throw the actionable message, not an NRE. Separate from the Theory
        // because the message-Contains assertion cannot take a null needle.
        var ex = Assert.Throws<InvalidOperationException>(
            () => TimeZoneAvailability.EnsureResolvable(null!, "ctx"));
        Assert.Contains("ctx", ex.Message);
        Assert.Contains("tz database", ex.Message);
    }
}
