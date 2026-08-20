namespace Cluckwork.Api.IntegrationTests;

using System.IO;

// #532 — a globally-scoped Identity user lookup is a tenant-isolation defect
// once one email can belong to several farms:
//
//   * UserStore.FindByEmailAsync is backed by SingleOrDefaultAsync, so it THROWS
//     the moment two farms share an address.
//   * UserStore.FindByNameAsync is backed by FirstOrDefaultAsync, so it silently
//     returns whichever farm Postgres orders first — a cross-tenant read.
//   * GetUsersInRoleAsync loads every user in the role across every farm and
//     leaves the caller to post-filter, which is O(all farms) and correct only
//     by convention.
//
// This is a TEXT SCAN, and its limits are stated rather than implied, because a
// guard that reads as safety while missing the common case is worse than none:
//
//   * It CANNOT see an unscoped `db.Users.Where(u => u.NormalizedEmail == x)`.
//     That expression contains none of the banned tokens and is exactly the
//     shape of the FirstRunAdminService defect this slice fixed. The real
//     protection there is behavioural — AccountScopedIdentityMigrationTests and
//     the cross-farm login tests.
//   * It CANNOT reliably ban the `UserManager.Users` IQueryable, because the
//     receiver is named by the caller (`users.Users`, `_userManager.Users`, …).
//     Banning the bare token `.Users` would match `db.Users`, which is legitimate
//     and used throughout. So that member is deliberately NOT on the list.
//
// What it does cover is the three method names above plus SignInManager, where a
// textual match is exact and the reintroduction it guards against is a
// copy-paste of a familiar Identity call.
public sealed class GlobalUserLookupGuardTests
{
    private static readonly string[] Banned =
    [
        "FindByEmailAsync",
        "FindByNameAsync",
        "FindByLoginAsync",
        "GetUsersInRoleAsync",
        "SignInManager",
    ];

    // The one type allowed to make an account-scoped lookup out of a global
    // primitive, plus break-glass, which is cross-account BY DESIGN (#265): an
    // operator recovering an account may not know which farm it is in, and
    // AdminRecoveryService already refuses an ambiguous email rather than
    // guessing (Recovery.Ambiguous).
    private static readonly string[] AllowedFiles =
    [
        "Cluckwork.Infrastructure/Identity/AccountUserDirectory.cs",
        "Cluckwork.Infrastructure/Identity/AdminRecoveryService.cs",
    ];

    // True when fileName[start..start+length) is all ASCII digits. Validating
    // every digit (not a sampled two) is what keeps a name like
    // "1abcdefgh9.Designer.cs" from passing the migration exemption.
    private static bool IsAllDigits(string s, int start, int length)
    {
        for (var i = start; i < start + length; i++)
            if (!char.IsDigit(s[i])) return false;
        return true;
    }

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cluckwork.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src");
    }

    [Fact]
    public void NoProductionCode_MakesAGloballyScopedIdentityUserLookup()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            // Path-relative, not just the file name: a future file called
            // AccountUserDirectory.cs anywhere else under src/ would otherwise
            // inherit the exemption silently.
            var relative = Path.GetRelativePath(SourceRoot(), file)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (AllowedFiles.Contains(relative)) continue;
            // Exempt only what EF GENERATES, and ONLY under a Migrations
            // directory. Two shapes, each matching exactly what `ef migrations`
            // mints — no looser "ends with" that a hand-written file could
            // slip through:
            //   * the model snapshot: exactly `<ContextName>ModelSnapshot.cs`
            //     (e.g. AppDbContextModelSnapshot.cs), or
            //   * a migration file: a full 14-digit timestamp followed by `_`
            //     and a name, optionally with a `.Designer.cs` suffix
            //     (e.g. 20260819202301_RequireUserIdentityColumns.cs and its
            //     .Designer.cs twin).
            // Both mention entity members, not calls, so a banned token there
            // is scaffold output, not a production lookup. A file named e.g.
            // UserLookupModelSnapshot.cs OUTSIDE a Migrations directory is a
            // production file and is NOT exempted: the directory + exact-name
            // requirement is what stops a hand-written class from inheriting
            // the exemption by name alone (round-3 / round-5 review).
            var fileName = Path.GetFileName(file);
            // "Under a Migrations directory": the directory component is exactly
            // "Migrations", so a sibling "Xmigrations" or a file merely named
            // *Migrations.cs cannot qualify.
            var dirName = Path.GetFileName(Path.GetDirectoryName(file));
            var underMigrations = string.Equals(dirName, "Migrations", StringComparison.Ordinal);
            if (underMigrations)
            {
                var snapshotName = "AppDbContext" + "ModelSnapshot.cs";
                if (fileName.Equals(snapshotName, StringComparison.Ordinal))
                    continue;
                // A full 14-digit timestamp (every one of them a digit), then
                // an underscore, then a non-empty name — the .Designer.cs twin
                // carries the same prefix and is covered by the same check.
                if (fileName.Length > 15
                    && fileName[14] == '_'
                    && IsAllDigits(fileName, 0, 14))
                    continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Comments discuss these APIs at length by design — this file does too.
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

                foreach (var banned in Banned)
                {
                    if (line.Contains(banned, StringComparison.Ordinal))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {trimmed}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Globally-scoped Identity user lookups found in src/. Route them through "
            + "IAccountUserDirectory, or add a deliberate exclusion with a reason:\n  "
            + string.Join("\n  ", offenders));
    }

    // #532 round 8 — the migration exemption is exercised by these two cases.
    // Rounds 4 and 5 tightened the exemption twice, but forcing
    // underMigrations = false and running the test in 15 ms proved nothing
    // exercises it at all. These tests write temporary files under a
    // Migrations directory and delete them in a finally.

    [Fact]
    public void MigrationShapedFile_ContainingBannedToken_IsExempt()
    {
        var srcRoot = SourceRoot();
        var migrationsDir = Path.Combine(srcRoot, "Cluckwork.Infrastructure", "Persistence", "Migrations");
        var fileName = "20990101000000_TempExemptionTest.cs";
        var filePath = Path.Combine(migrationsDir, fileName);
        try
        {
            Directory.CreateDirectory(migrationsDir);
            File.WriteAllText(filePath,
                "namespace T;\npublic class TempExemptionTest\n{\n    public const string S = \"FindByEmailAsync\";\n}\n");

            // Force underMigrations = false by renaming to a non-migration name
            // in a non-Migrations directory, proving the guard CAUGHT it there.
            var prodDir = Path.Combine(srcRoot, "Cluckwork.Infrastructure", "Identity");
            var prodPath = Path.Combine(prodDir, "20990101000000_TempExemptionTest.cs");
            File.Move(filePath, prodPath);
            var caughtOffenders = ScanSingleFile(prodPath);
            Assert.True(caughtOffenders.Count > 0,
                "same filename OUTSIDE a Migrations directory must be CAUGHT");
            File.Move(prodPath, filePath);

            // Back under Migrations with the migration-shaped name: EXEMPT.
            var exemptOffenders = ScanSingleFile(filePath);
            Assert.True(exemptOffenders.Count == 0,
                "migration-shaped file under Migrations/ must be EXEMPT");
        }
        finally
        {
            foreach (var p in new[] { filePath, Path.Combine(srcRoot, "Cluckwork.Infrastructure", "Identity", "20990101000000_TempExemptionTest.cs") })
                if (File.Exists(p)) File.Delete(p);
        }
    }

    [Fact]
    public void ProductionShapedSnapshot_ContainingBannedToken_IsCaught()
    {
        var srcRoot = SourceRoot();
        var migrationsDir = Path.Combine(srcRoot, "Cluckwork.Infrastructure", "Persistence", "Migrations");
        var fileName = "UserLookupModelSnapshot.cs";
        var filePath = Path.Combine(migrationsDir, fileName);
        try
        {
            Directory.CreateDirectory(migrationsDir);
            File.WriteAllText(filePath,
                "namespace T;\npublic class UserLookupModelSnapshot\n{\n    public const string S = \"FindByEmailAsync\";\n}\n");

            var offenders = ScanSingleFile(filePath);
            Assert.True(offenders.Count > 0,
                "UserLookupModelSnapshot.cs (production-shaped, not the EF-generated "
                + "AppDbContextModelSnapshot.cs) must be CAUGHT even under Migrations/");
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }

    // Scans a single file with the same exemption logic as the main guard.
    // Returns the list of offending lines (empty = exempt or clean).
    private static List<string> ScanSingleFile(string filePath)
    {
        var offenders = new List<string>();
        var fileName = Path.GetFileName(filePath);
        var dirName = Path.GetFileName(Path.GetDirectoryName(filePath));
        var underMigrations = string.Equals(dirName, "Migrations", StringComparison.Ordinal);
        if (underMigrations)
        {
            if (fileName.Equals("AppDbContext" + "ModelSnapshot.cs", StringComparison.Ordinal))
                return offenders;
            if (fileName.Length > 15 && fileName[14] == '_' && IsAllDigits(fileName, 0, 14))
                return offenders;
        }
        var lines = File.ReadAllLines(filePath);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
            foreach (var banned in Banned)
                if (line.Contains(banned, StringComparison.Ordinal))
                    offenders.Add($"{fileName}:{i + 1}  {trimmed}");
        }
        return offenders;
    }
}
