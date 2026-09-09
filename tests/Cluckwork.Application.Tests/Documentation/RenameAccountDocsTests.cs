namespace Cluckwork.Application.Tests.Documentation;

using System.Diagnostics;

public sealed class RenameAccountDocsTests
{
    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData("docs/decisions/README.md")]
    public void RenameDecision_IsLinkedFromRulesAndIndex(string relativePath)
    {
        Assert.Contains("732-farm-code-rename.md", File.ReadAllText(Path.Combine(RepoRoot(), relativePath)));
    }

    [Fact]
    public void RenameRunbook_HasExecutableExamplesAndProvisioningDrill()
    {
        var runbook = File.ReadAllText(Path.Combine(RepoRoot(), "docs/runbooks/provisioning-a-new-farm.md"));
        var renameSection = runbook[runbook.IndexOf("## Renaming the default farm's code", StringComparison.Ordinal)..];

        Assert.Contains("docker run --rm --env-file", renameSection);
        Assert.DoesNotContain("immutable", renameSection, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("## Provisioning drill", runbook.Split('\n').Last(line => line.StartsWith("## ", StringComparison.Ordinal)).TrimEnd('\r'));
    }

    private static string RepoRoot() => Git("rev-parse --show-toplevel", AppContext.BaseDirectory).Trim();

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
