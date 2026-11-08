using System.Text.RegularExpressions;
using Cluckwork.Application.Tests.Documentation;

namespace Cluckwork.Application.Tests.Architecture;

// #1281 G6.7: only the Owner two-factor gate's grace start writes
// SecondFactorRequiredSince (G3.20 holds src/). No script, harness or compose
// file under tools/ or deploy/ may name the column, so none can set a start
// that postpones or ends an Owner's grace.
public sealed class OwnerGraceWriterTests
{
    // 126 tracked files on 2026-11-07.
    private const int FileFloor = 100;

    // 87 tracked files under tools/simulation/ on 2026-11-08.
    private const int SimulationFileFloor = 70;

    [Fact]
    public void NoToolOrDeployFile_NamesTheGraceColumn()
    {
        var root = TenancyDocsFreshnessTests.RepoRoot();
        var files = TenancyDocsFreshnessTests.TrackedFiles(root)
            .Where(path => path.StartsWith("tools/", StringComparison.Ordinal) || path.StartsWith("deploy/", StringComparison.Ordinal))
            .ToList();

        Assert.True(files.Count >= FileFloor, $"the walk visited {files.Count} files under tools/ and deploy/, below its floor of {FileFloor}");
        var naming = files.Where(path => File.ReadAllText(Path.Combine(root, path))
            .Contains("SecondFactorRequiredSince", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(naming.Count == 0, "these files name SecondFactorRequiredSince:\n  " + string.Join("\n  ", naming));
    }

    // G6.8: the simulation app service reads only its own environment block,
    // which sets no grace, so the stack runs the shipped 14 days and no env file
    // can raise them. Comment lines are skipped; one names env_file on purpose.
    [Fact]
    public void TheSimulationAppService_ReadsNoEnvFile_AndSetsNoGrace()
    {
        var lines = File.ReadAllLines(Path.Combine(TenancyDocsFreshnessTests.RepoRoot(), "tools", "simulation", "docker-compose.sim.yml"));
        var start = Array.IndexOf(lines, "  app:");
        Assert.True(start >= 0, "docker-compose.sim.yml has no app service");
        var service = lines.Skip(start + 1)
            .TakeWhile(line => !Regex.IsMatch(line, @"^  \S"))
            .Where(line => !line.TrimStart().StartsWith('#'))
            .ToList();

        Assert.Contains(service, line => Regex.IsMatch(line, @"^    environment:\s*$"));
        Assert.DoesNotContain(service, line => Regex.IsMatch(line, @"^\s+env_file:"));
        Assert.DoesNotContain(service, line => line.Contains("OwnerGraceDays", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoSimulationFile_SetsTheGrace()
    {
        var root = TenancyDocsFreshnessTests.RepoRoot();
        var files = TenancyDocsFreshnessTests.TrackedFiles(root)
            .Where(path => path.StartsWith("tools/simulation/", StringComparison.Ordinal)).ToList();

        Assert.True(files.Count >= SimulationFileFloor, $"the walk visited {files.Count} files under tools/simulation/, below its floor of {SimulationFileFloor}");
        var naming = files.Where(path => File.ReadAllText(Path.Combine(root, path))
            .Contains("OwnerGraceDays", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(naming.Count == 0, "these files set or name OwnerGraceDays:\n  " + string.Join("\n  ", naming));
    }
}
