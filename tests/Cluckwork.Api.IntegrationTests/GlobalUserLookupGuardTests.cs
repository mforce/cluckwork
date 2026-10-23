namespace Cluckwork.Api.IntegrationTests;

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
//
// #1276 — the same scan, with its own list and no allowed file, refuses
// Identity's two-factor API: UserManager's authenticator and recovery-code
// methods and the recovery-code store's methods, which would write
// "[AspNetUserStore]" rows outside TwoFactorCredentials' protect site (#1274).
// The store's GetTokenAsync, SetTokenAsync and RemoveTokenAsync are not listed:
// HttpContext.GetTokenAsync shares the name. Both lists skip build output
// (bin/ and obj/) and migration-shaped files: an EF migration or snapshot name
// under a Migrations directory.
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

    // TheTwoFactorList_IsExactlyThePinnedNames pins this list against the
    // literal names below; keep the two lists separate.
    private static readonly string[] TwoFactorApi =
    [
        "SetAuthenticatorKeyAsync",
        "ResetAuthenticatorKeyAsync",
        "GetAuthenticatorKeyAsync",
        "GenerateNewTwoFactorRecoveryCodesAsync",
        "RedeemTwoFactorRecoveryCodeAsync",
        "CountRecoveryCodesAsync",
        "SetAuthenticationTokenAsync",
        "GetAuthenticationTokenAsync",
        "RemoveAuthenticationTokenAsync",
        "ReplaceCodesAsync",
        "RedeemCodeAsync",
        "CountCodesAsync",
    ];

    // The one type allowed to make an account-scoped lookup out of a global
    // primitive, plus break-glass, which is cross-account BY DESIGN (#265): an
    // operator recovering an account may not know which farm it is in, and
    // AdminRecoveryService already refuses an ambiguous email rather than
    // guessing (Recovery.Ambiguous).
    private static readonly string[] AllowedFiles =
    [
        "Cluckwork.Infrastructure/Modules/Access/Identity/AccountUserDirectory.cs",
        "Cluckwork.Infrastructure/Modules/Access/Identity/AdminRecoveryService.cs",
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
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cluckwork.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src");
    }

    // On 2026-10-22 src/ held 694 source files outside bin/ and obj/, and the
    // scans read 639 of them after the migration-shaped skip (the lookup scan
    // 637, after its two allowed files). The floor leaves about 14% headroom
    // for deletions; a scan below it has lost files, and one that reads none
    // would pass vacuously.
    private const int SourceFileFloor = 550;

    private const string TwoFactorCredentialsFile = "Cluckwork.Infrastructure/Modules/Access/Identity/TwoFactorCredentials.cs";

    // Both walks list files here. Build output is skipped, so the count depends
    // only on the checkout.
    private static List<string> SourceFiles()
    {
        var root = SourceRoot();
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .ToList();
    }

    // Checked on the files the scanner read, after its own skips, so a skip that
    // drops TwoFactorCredentials.cs or a subtree fails here.
    private static void AssertScanned(List<string> scanned)
    {
        Assert.True(scanned.Contains(TwoFactorCredentialsFile),
            $"the scan did not read {TwoFactorCredentialsFile}. If the file moved or was renamed, "
            + "update TwoFactorCredentialsFile here. If the scanner now skips it, the two-factor ban "
            + "no longer covers the one file that owns two-factor state.");
        Assert.True(scanned.Count >= SourceFileFloor,
            $"the scan read {scanned.Count} source files under src/, below its floor of {SourceFileFloor}");
    }

    [Fact]
    public void NoProductionCode_MakesAGloballyScopedIdentityUserLookup()
    {
        var scanned = new List<string>();
        var offenders = ScanFiles(SourceFiles(), Banned, AllowedFiles, scanned);

        AssertScanned(scanned);

        Assert.True(offenders.Count == 0,
            "Globally-scoped Identity user lookups found in src/. Route them through "
            + "IAccountUserDirectory, or add a deliberate exclusion with a reason:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void NoProductionCode_CallsIdentitysTwoFactorApi()
    {
        var scanned = new List<string>();
        var offenders = ScanForTwoFactorApi(SourceFiles(), scanned);

        AssertScanned(scanned);
        Assert.True(offenders.Count == 0,
            "Identity's two-factor API found in src/. Two-factor state is written only by "
            + "TwoFactorCredentials (#1274); only build output and migration-shaped files are skipped:\n  "
            + string.Join("\n  ", offenders));
    }

    // The real walk and the fixtures below share this call, so the list the
    // walk scans is the list the fixtures prove.
    private static List<string> ScanForTwoFactorApi(IEnumerable<string> files, List<string>? scanned = null) =>
        ScanFiles(files, TwoFactorApi, allowedFiles: [], scanned);

    // Literals, never TwoFactorApi: deleting a name from the list must turn a
    // test red.
    private static readonly string[] PinnedTwoFactorNames =
    [
        "SetAuthenticatorKeyAsync",
        "ResetAuthenticatorKeyAsync",
        "GetAuthenticatorKeyAsync",
        "GenerateNewTwoFactorRecoveryCodesAsync",
        "RedeemTwoFactorRecoveryCodeAsync",
        "CountRecoveryCodesAsync",
        "SetAuthenticationTokenAsync",
        "GetAuthenticationTokenAsync",
        "RemoveAuthenticationTokenAsync",
        "ReplaceCodesAsync",
        "RedeemCodeAsync",
        "CountCodesAsync",
    ];

    public static TheoryData<string> TwoFactorTokens => [.. PinnedTwoFactorNames];

    [Fact]
    public void TheTwoFactorList_IsExactlyThePinnedNames()
    {
        Assert.Equal(PinnedTwoFactorNames.Order(), TwoFactorApi.Order());
    }

    // Proves each token reaches the scanner, and that both migration-shaped
    // names (a timestamped migration and the model snapshot under Migrations/)
    // are skipped. The probes live in a temporary directory, outside src/.
    [Theory]
    [MemberData(nameof(TwoFactorTokens))]
    public void EachTwoFactorToken_IsCaughtInSource_AndSkippedInAMigrationFile(string token)
    {
        var root = Directory.CreateTempSubdirectory("cw-1276-");
        try
        {
            var source = Path.Combine(root.FullName, "Identity", "Probe.cs");
            var migration = Path.Combine(root.FullName, "Migrations", "20990101000000_Probe.cs");
            var snapshot = Path.Combine(root.FullName, "Migrations", "AppDbContextModelSnapshot.cs");
            foreach (var path in new[] { source, migration, snapshot })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, $"class Probe {{ void M(dynamic users) => users.{token}(null); }}\n");
            }

            Assert.Single(ScanForTwoFactorApi([source]));
            Assert.Empty(ScanForTwoFactorApi([migration]));
            Assert.Empty(ScanForTwoFactorApi([snapshot]));
        }
        finally
        {
            root.Delete(recursive: true);
        }
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
            var prodDir = Path.Combine(srcRoot, "Cluckwork.Infrastructure", "Modules", "Access", "Identity");
            var prodPath = Path.Combine(prodDir, "20990101000000_TempExemptionTest.cs");
            File.Move(filePath, prodPath);
            var caughtOffenders = ScanFiles([prodPath]);
            Assert.True(caughtOffenders.Count > 0,
                "same filename OUTSIDE a Migrations directory must be CAUGHT");
            File.Move(prodPath, filePath);

            // Back under Migrations with the migration-shaped name: EXEMPT.
            var exemptOffenders = ScanFiles([filePath]);
            Assert.True(exemptOffenders.Count == 0,
                "migration-shaped file under Migrations/ must be EXEMPT");
        }
        finally
        {
            foreach (var p in new[] { filePath, Path.Combine(srcRoot, "Cluckwork.Infrastructure", "Modules", "Access", "Identity", "20990101000000_TempExemptionTest.cs") })
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

            var offenders = ScanFiles([filePath]);
            Assert.True(offenders.Count > 0,
                "UserLookupModelSnapshot.cs (production-shaped, not the EF-generated "
                + "AppDbContextModelSnapshot.cs) must be CAUGHT even under Migrations/");
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }

    // This is the guard's single scanner. The repository-wide assertion and
    // focused fixture tests deliberately exercise this same code path.
    private static List<string> ScanFiles(IEnumerable<string> files) => ScanFiles(files, Banned, AllowedFiles);

    private static List<string> ScanFiles(
        IEnumerable<string> files, string[] tokens, string[] allowedFiles, List<string>? scanned = null)
    {
        var offenders = new List<string>();
        var sourceRoot = SourceRoot();

        foreach (var file in files)
        {
            // Path-relative, not just the file name: a future file called
            // AccountUserDirectory.cs anywhere else under src/ would otherwise
            // inherit the exemption silently.
            var relative = Path.GetRelativePath(sourceRoot, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (allowedFiles.Contains(relative)) continue;

            // Exempt only migration-shaped files, and ONLY under a Migrations
            // directory: the two names `ef migrations` mints, matched exactly,
            // with no looser "ends with". A hand-written file with one of these
            // names under Migrations/ is skipped too; the shapes are:
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

            scanned?.Add(relative);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Comments discuss these APIs at length by design — this file does too.
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

                foreach (var token in tokens)
                {
                    if (line.Contains(token, StringComparison.Ordinal))
                        offenders.Add($"{fileName}:{i + 1}  {trimmed}");
                }
            }
        }

        return offenders;
    }
}
