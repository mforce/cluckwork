// #1277 N3: stand-ins for Coverlet's injected tracker, so RedactedToStringTests
// can pin each condition of the scanner's coverage-counter allowance without a
// coverage run. Coverlet names its tracker
// Coverlet.Core.Instrumentation.Tracker.<assembly>_<guid>; CW1003 keeps product
// code in src/ out of that namespace, and this file is test-only.
namespace Coverlet.Core.Instrumentation.Tracker.CluckworkFixture;

internal static class Counter
{
    public static void RecordHit(int id) => _ = id;
}

internal static class WrongName
{
    public static void RecordHits(int id) => _ = id;
}

internal sealed class Instance
{
    public void RecordHit(int id) => _ = id;
}

internal static class ReturnsValue
{
    public static int RecordHit(int id) => id;
}

internal static class TakesLong
{
    public static void RecordHit(long id) => _ = id;
}
