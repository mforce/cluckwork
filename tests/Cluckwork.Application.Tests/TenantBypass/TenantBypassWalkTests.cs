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
