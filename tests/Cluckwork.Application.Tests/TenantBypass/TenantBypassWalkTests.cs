namespace Cluckwork.Application.Tests.TenantBypass;

using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// #536 Part 1 — the walk. These tests prove the scanner actually sees the
// tree (a zero-occurrence result is a scan failure, not a clean bill — review
// M2), and they pin the current occurrence baseline so a future shrink of the
// walked surface is a finding, not a silent green.
public sealed class TenantBypassWalkTests
{
    private static string SrcRoot() =>
        Path.Combine(GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found"), "src");

    [Fact]
    public void Walk_SeesTheWholeSourceTree()
    {
        var srcRoot = SrcRoot();
        var files = GuardScanner.EnumerateSourceFiles(srcRoot);

        // Floor: the walk must see at least this many files. The current
        // source tree is ~200 .cs files; a floor of 150 leaves headroom for
        // growth while still catching a root-resolution or exclusion bug
        // (which would yield a handful of files, or zero).
        Assert.True(files.Count >= 150,
            $"walk saw only {files.Count} .cs files under src/ — the floor is 150. The scanner is not seeing the tree.");

        // The walk must include the known bypass-heavy files. If any of these
        // disappears from the walk, the exclusion logic is eating real source.
        var known = files.Select(f => f.Replace('\\', '/'));
        Assert.Contains(known, f => f.EndsWith("src/Cluckwork.Infrastructure/Repositories/EggLotRepository.cs"));
        Assert.Contains(known, f => f.EndsWith("src/Cluckwork.Infrastructure/Identity/IdentityProvider.cs"));
    }

    [Fact]
    public void Walk_ParsesEveryFileWithoutErrors()
    {
        var report = GuardScanner.Scan(SrcRoot(), AllowListPath());

        Assert.Empty(report.ParseErrors);
    }

    // False-green proof for the parse-error guard: a file with a syntax error
    // MUST surface in report.ParseErrors (and thus fail Evaluate). If the walk
    // silently skipped an unparseable file, the parse-error guard would be
    // green and the walk untrustworthy — this mutation proves it reds.
    [Fact]
    public void ParseError_IsSurfacedNotSwallowed()
    {
        var dir = Directory.CreateTempSubdirectory("t-parse-").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        // A genuinely unparseable file: an unclosed brace. (Note: `void X( )`
        // with a space is VALID C# — the parser accepts it, so it would NOT
        // surface a parse error. The unclosed brace is the real syntax error.)
        File.WriteAllText(Path.Combine(dir, "src", "Bad.cs"),
            "namespace Bad;\npublic class Broken { void X() { "); // unclosed brace

        var report = GuardScanner.Scan(Path.Combine(dir, "src"), Path.Combine(dir, "none.json"));

        Assert.NotEmpty(report.ParseErrors);
        var failures = GuardScanner.Evaluate(report);
        Assert.Contains(failures, f => f.Contains("parse error"));
    }

    // False-green proof for the file-count floor: the real-tree floor must be a
    // STATIC minimum, not a tautology of the scan's own count. If the floor
    // were files.Count, a walk that silently excluded a subtree would still
    // pass. This asserts the floor is below the actual count (headroom) and is
    // a const, so the gate has teeth.
    [Fact]
    public void RealTreeFileFloor_IsMeaningfulNotTautological()
    {
        var actual = GuardScanner.EnumerateSourceFiles(SrcRoot()).Count;

        // The floor must be a real minimum the tree exceeds, not equal to it.
        Assert.True(GuardScanner.RealTreeFileFloor < actual,
            $"RealTreeFileFloor ({GuardScanner.RealTreeFileFloor}) is not below the actual count ({actual}) — " +
            "the floor has no headroom and cannot catch a walk that excluded a subtree.");
        // And it must be a substantial fraction of the tree, so a missing
        // top-level directory (which would drop the count by tens of files) reds.
        Assert.True(GuardScanner.RealTreeFileFloor >= actual / 2,
            $"RealTreeFileFloor ({GuardScanner.RealTreeFileFloor}) is less than half the tree ({actual}) — " +
            "it would not catch a walk that missed a whole project directory.");
    }

    [Fact]
    public void Walk_FindsTheIgnoreQueryFiltersBaseline()
    {
        var report = GuardScanner.Scan(SrcRoot(), AllowListPath());
        var iqf = report.Occurrences.Where(o => o.Kind == BypassKind.IgnoreQueryFilters).ToList();

        // Baseline pinned 2026-08-22: 36 code occurrences (52 total grep hits
        // minus 16 comment-only lines — comments are structurally absent from
        // the syntax tree, which is exactly why the false-positive control in
        // the mutation matrix holds). A DROP below the floor means the walk
        // lost occurrences; a rise is expected with new code and is excused
        // through the allow-list, not by moving this number.
        Assert.True(iqf.Count >= 36,
            $"only {iqf.Count} IgnoreQueryFilters occurrences found — baseline is 36. The walk is losing occurrences.");

        // EggLotRepository carries 3 of them — a named spot-check that the
        // count is not a happy accident of one file.
        Assert.Contains(iqf, o => o.File.EndsWith("EggLotRepository.cs") && o.Line > 0);
    }

    [Fact]
    public void Walk_FindsRawSqlAndIdentityBans()
    {
        var report = GuardScanner.Scan(SrcRoot(), AllowListPath());

        // 8 src files use raw-SQL APIs (FromSql*/ExecuteSql*/SqlQuery) — the
        // FOR UPDATE/audit paths. The walk must find at least their
        // statements.
        var rawSql = report.Occurrences.Where(o => o.Kind == BypassKind.RawSql).ToList();
        Assert.True(rawSql.Count >= 8,
            $"only {rawSql.Count} raw-SQL occurrences found — expected at least 8 (the FOR UPDATE/audit paths).");

        // Identity string-lookups: the ONLY legitimate call sites are inside
        // AccountUserDirectory (which reimplements them scoped). Every other
        // occurrence is a real find. Today's expectation: zero outside that
        // file — but the walk must still REPORT any it finds (Task 3's
        // allow-list decides what is excused).
        var identity = report.Occurrences
            .Where(o => o.Kind is BypassKind.IdentityLookup or BypassKind.SignInManager or BypassKind.UserManagerUsers)
            .ToList();
        // The seeder comments mention these names but comments are not in the
        // syntax tree, so a non-zero count here is real code. Record whatever
        // exists; Task 3 allow-lists or fixes each.
        _ = identity; // exercised by the real-tree test in Task 3
    }

    private static string AllowListPath() =>
        Path.Combine(AppContext.BaseDirectory, "Data", "tenant-bypass-allowlist.json");
}
