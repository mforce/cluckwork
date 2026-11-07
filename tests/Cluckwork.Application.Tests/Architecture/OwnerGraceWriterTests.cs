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
}
