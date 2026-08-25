namespace Cluckwork.Application.Tests.Documentation;

using System.Diagnostics;
using System.Text.RegularExpressions;

public sealed class TenancyDocsFreshnessTests
{
    private static readonly Regex Stale = new(
        // Any short run of words between the two, so 'multi-tenant infrastructure
        // dormant', 'multi-tenant infra present but dormant' and 'multi-tenant
        // infrastructure is dormant' all match. [^.\n] keeps it inside one
        // sentence so it cannot span unrelated prose.
        @"(multi-?tenant[^.\n]{0,40}?dormant" +
        @"|dormant[^.\n]{0,40}?multi-?tenant" +
        @"|kept\s+dormant\s+behind\s+one\s+default\s+farm" +
        @"|email\s+uniqueness\s+is\s+global" +
        // Added after review round 1 found specs/technical/tech_spec.md, whose
        // stale claims said 'single-tenant today' rather than 'dormant' — the
        // same concept in different words, which is why the guard now matches
        // several phrasings of it.
        @"|single-?tenant\s+(deploy|mode|today|now)" +
        @"|runs?\s+exactly\s+one\s+account" +
        @"|deployment\s+runs\s+exactly\s+one\s+farm" +
        @"|invisible\s+in\s+single-?tenant" +
        @"|globally\s+unique\s+email)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void NoTrackedFileDescribesTenancyAsDormant()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        foreach (var relative in TrackedFiles(root))
        {
            // The dated graphify-out/ snapshots DO match this pattern (10 of them
            // carry 'multi-tenant infra present but dormant' in a graph node's
            // rationale). They are generated, point-in-time records: they must not
            // be rewritten, and they cannot be kept fresh, so they are excluded by
            // path. Mutation K3 (delete this line) proves the exclusion is
            // load-bearing — without it the test is red against those snapshots.
            if (relative.StartsWith("graphify-out/", StringComparison.Ordinal)) continue;
            if (relative.EndsWith("TenancyDocsFreshnessTests.cs", StringComparison.Ordinal)) continue;
            var full = Path.Combine(root, relative);
            if (!File.Exists(full)) continue;
            string text;
            try { text = File.ReadAllText(full); } catch (IOException) { continue; }
            var match = Stale.Match(text);
            if (match.Success) offenders.Add($"{relative}: \"{match.Value}\"");
        }
        Assert.True(offenders.Count == 0,
            "Tracked files still describe multi-tenancy as dormant:\n  " + string.Join("\n  ", offenders));
    }

    // `git rev-parse`, not a walk looking for a .git DIRECTORY: in a git worktree
    // .git is a FILE, so Directory.Exists walks past the root and returns null.
    // This repo is worked in worktrees, so that is a real path, not a hypothetical.
    private static string RepoRoot() => Git("rev-parse --show-toplevel", AppContext.BaseDirectory).Trim();

    private static IEnumerable<string> TrackedFiles(string root) =>
        Git("ls-files", root)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);

    private static string Git(string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi);
        Assert.NotNull(p);
        var output = p!.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return output;
    }
}
