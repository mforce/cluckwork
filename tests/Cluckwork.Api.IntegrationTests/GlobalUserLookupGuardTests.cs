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
            // Generated EF migration snapshots mention entity members, not calls.
            if (file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

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
}
