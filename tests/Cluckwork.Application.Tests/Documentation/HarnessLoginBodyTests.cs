namespace Cluckwork.Application.Tests.Documentation;

// #1278 G3.21: an enrolled user's failed login carries a sign-in challenge,
// which is a credential, so no harness keeps or prints a login's body; it
// reports the status and the problem title. The walk reads every tracked file
// under tools/simulation and tools/verify that signs in.
public sealed class HarnessLoginBodyTests
{
    // On 2026-10-28 nine tracked files there name the login route.
    private const int LoginFileFloor = 4;

    private static readonly string[] SignsIn = ["/auth/login", "AUTH_PATHS.LOGIN"];

    // Each prints or keeps a whole response body.
    private static readonly string[] BodyDumps = ["${res.body}", "exc.read().decode()", "await res.text()", "response.text()"];

    [Fact]
    public void NoHarnessThatSignsIn_KeepsOrPrintsAResponseBody()
    {
        var root = TenancyDocsFreshnessTests.RepoRoot();
        var files = TenancyDocsFreshnessTests.TrackedFiles(root)
            .Where(p => p.StartsWith("tools/simulation/", StringComparison.Ordinal) || p.StartsWith("tools/verify/", StringComparison.Ordinal))
            .Select(p => (Path: p, Text: File.ReadAllText(Path.Combine(root, p))))
            .Where(f => SignsIn.Any(route => f.Text.Contains(route, StringComparison.Ordinal)))
            .ToList();

        Assert.True(files.Count >= LoginFileFloor, $"the walk found {files.Count} files that sign in, below its floor of {LoginFileFloor}");
        var offenders = files
            .SelectMany(f => BodyDumps.Where(dump => f.Text.Contains(dump, StringComparison.Ordinal)).Select(dump => $"{f.Path}: {dump}"))
            .ToList();
        Assert.True(offenders.Count == 0, "a harness that signs in prints or keeps a response body:\n  " + string.Join("\n  ", offenders));
    }
}
