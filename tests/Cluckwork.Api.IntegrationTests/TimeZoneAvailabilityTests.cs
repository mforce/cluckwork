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
        // Doubles as a build/CI-environment canary: if the runtime ever loses its
        // tz database (a chiseled image, InvariantGlobalization=true), this fails —
        // exactly the fleet-wide breakage #264 exists to catch early.
        var ex = Record.Exception(() =>
            TimeZoneAvailability.EnsureResolvable(TimeZoneAvailability.CanaryZoneId, "canary"));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("Not/AZone")]
    [InlineData("   ")]
    public void EnsureResolvable_UnusableId_ThrowsWithActionableMessage(string badId)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => TimeZoneAvailability.EnsureResolvable(badId, "ctx"));
        Assert.Contains("ctx", ex.Message);
        Assert.Contains(badId, ex.Message);
        Assert.Contains("tz data", ex.Message);
    }
}
