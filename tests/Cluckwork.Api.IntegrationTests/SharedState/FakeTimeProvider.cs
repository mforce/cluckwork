namespace Cluckwork.Api.IntegrationTests.SharedState;

// #543 — manually-advanced clock for the shared-state contract suites.
internal sealed class FakeTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
