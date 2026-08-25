namespace Cluckwork.Application.Tests.Documentation;

using System.Diagnostics;
using System.Text.RegularExpressions;

public sealed class TenancyDocsFreshnessTests
{
    private static readonly Regex Stale = new(
        @"(multi-?tenant\w*\s+(infrastructure\s+)?(is\s+)?dormant" +
        @"|dormant\s+multi-?tenant" +
        @"|kept\s+dormant\s+behind\s+one\s+default\s+farm" +
        @"|email\s+uniqueness\s+is\s+global" +
        @"|globally\s+unique\s+email)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void NoTrackedFileDescribesTenancyAsDormant()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        foreach (var relative in TrackedFiles(root))
        {
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
