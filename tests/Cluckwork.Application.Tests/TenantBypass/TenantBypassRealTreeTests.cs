namespace Cluckwork.Application.Tests.TenantBypass;

using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// #536 Part 1 — the real-tree gate. This is the guard that runs in the
// pre-commit hook and CI: it scans the ACTUAL src/ tree, and fails the build
// when any bypass is not on the committed allow-list (with a justification),
// or when an allow-list entry has gone stale (its site was deleted but the
// exemption was left behind).
//
// This is the test the mutation matrix aims at: a real-tree mutant (an
// unlisted bypass, a dropped AccountId compare, a wrapper) reds HERE, on its
// named assertion — not the temp-tree tests, which exercise the same
// semantics against fixtures.
public sealed class TenantBypassRealTreeTests
{
    private static string SrcRoot() =>
        Path.Combine(GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found"), "src");

    private static string AllowListPath() =>
        Path.Combine(AppContext.BaseDirectory, "TenantBypass", "Data", "tenant-bypass-allowlist.json");

    // The build gate. Every failure message names the offending site or
    // stale entry, so a red build tells you exactly what to fix or excuse.
    [Fact]
    public void RealSourceTree_AllBypassesAreAllowListed()
    {
        var report = GuardScanner.Scan(SrcRoot(), AllowListPath());
        var failures = GuardScanner.Evaluate(report);
        Assert.True(failures.Count == 0,
            "tenant-bypass guard failed:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void AtomicLogoutCte_BothUpdateArmsRetainOwnerScope()
    {
        const string symbol =
            "Cluckwork.Infrastructure.Identity.IdentityProvider.ExecuteLineageFenceAsync(string currentHash, " +
            "string[] ancestorHashes, Guid rootUserId, Guid rootAccountId, int rootIssuedEpoch, " +
            "DateTimeOffset now, string rotatedStamp, CancellationToken ct)";
        var report = GuardScanner.Scan(SrcRoot(), AllowListPath());
        var occurrence = Assert.Single(report.Occurrences, candidate =>
            candidate.Kind == BypassKind.RawSql
            && candidate.EnclosingSymbol == symbol
            && candidate.Detail == "IRawSqlCommandBuilder.Build");

        Assert.DoesNotContain(report.RawSqlExecutionViolations, violation =>
            violation.Contains(symbol, StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(occurrence.RawSqlText));

        var violations = GuardScanner.FindScopedUpdateArmViolations(
            occurrence.RawSqlText!,
            expectedUpdateArmCount: 2,
            ("UserId", "rootUserId"),
            ("AccountId", "rootAccountId"),
            ("IssuedEpoch", "rootIssuedEpoch"));
        Assert.True(violations.Count == 0,
            "atomic logout CTE owner-scope guard failed:\n  " + string.Join("\n  ", violations));
    }

    // The filter-free-set leg against the REAL tree — a STABILITY test, not a
    // shape gate. The scanner finds every db.<Table> access where <Table> is a
    // filter-free entity carrying an AccountId column (a tenant table the
    // global query filter does NOT scope) and whose enclosing statement has no
    // AccountId compare. Each such candidate is classified in
    // Data/filter-free-set-sites.tsv with the REASON it is scoped (by-id,
    // by-hash, non-tenant sweep, …).
    //
    // Why stability and not a pure shape gate: the shape check cannot
    // distinguish a by-id/by-hash/caller-scoped query (safe) from an unscoped
    // one (a real tenant leak) — reviewer M4/F4 named this ("shape, not
    // provenance"). So the leg records the classifications and fails only on
    // DRIFT: a new unclassified candidate, a disappeared site, or a
    // needs-review sentinel. Nothing is silently un-banned — a new unscoped
    // db.<tenant-table> query cannot land without a classification decision.
    [Fact]
    public void RealSourceTree_FilterFreeSetSitesAreStableAndClassified()
    {
        // Tenant-table filter-free property names: filter-free entities that
        // (a) have a DbSet on AppDbContext and (b) carry an AccountId
        // property. Non-tenant filter-free tables (Roles, RoleClaims,
        // DurableJobs) and in-memory domain collections (user.Roles) are
        // excluded here — the ban on them is the discovery floor's job, and
        // an in-memory `user.Roles` is not a DB query at all (the scanner's
        // receiver check drops it).
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=unreachable;Username=unreachable;Password=unreachable")
                .Options,
            new TenantContext(), new FlockScope());

        var filterFreeEntityTypes = db.Model.GetEntityTypes()
            .Where(e => e.GetDeclaredQueryFilters().Count == 0)
            .Select(e => e.ClrType)
            .ToHashSet();

        // ALL filter-free DbSet properties (tenant + non-tenant). Review P1-2:
        // restricting this to sets with an AccountId column left the non-tenant
        // filter-free sets (UserRoles, Roles, UserClaims, UserLogins, UserTokens,
        // RoleClaims, DurableJobs) invisible to the guard — a future unscoped
        // db.UserRoles query would pass silently. Walk every filter-free DbSet
        // and give the two categories separate classification handling below.
        var allPropertyNames = typeof(AppDbContext).GetProperties()
            .Where(p => p.PropertyType.IsGenericType
                && p.PropertyType.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.DbSet<>)
                && filterFreeEntityTypes.Contains(p.PropertyType.GetGenericArguments()[0]))
            .Select(p => p.Name)
            .ToList();

        // Split into tenant sets (the CLR entity type declares an AccountId
        // property — Users, RefreshTokens, IdempotencyRecord) and non-tenant
        // sets (it does not — the Identity claim/login/token/join tables, Roles,
        // RoleClaims, DurableJob). The split is on the CLR SHAPE, deliberately,
        // and since #670 that is no longer the same thing as the table's column
        // set: AspNetUserRoles now carries a SHADOW AccountId column (stamped
        // and tokened by the write guards, FK-bound to the user's farm) that
        // reflection cannot see, and it STAYS on the non-tenant track — the
        // stricter one, where every db.UserRoles access is a candidate needing
        // a classification rather than only the ones with no AccountId compare.
        // A future shadow tenant column on another Identity table lands on the
        // same track for the same reason; moving a set to the tenant track is a
        // decision to take here, not a side effect of a model change.
        var tenantNames = new List<string>();
        var nonTenantNames = new List<string>();
        foreach (var name in allPropertyNames)
        {
            var elementType = typeof(AppDbContext).GetProperty(name)!
                .PropertyType.GetGenericArguments()[0];
            if (elementType.GetProperty("AccountId") != null)
            {
                tenantNames.Add(name);
            }
            else
            {
                nonTenantNames.Add(name);
            }
        }

        Assert.True(tenantNames.Count > 0,
            "expected at least one tenant-table filter-free DbSet property (e.g. Users, RefreshTokens)");
        Assert.True(nonTenantNames.Count > 0,
            "expected at least one non-tenant filter-free DbSet property (e.g. Roles, UserRoles) — " +
            "the non-tenant track exists to enumerate them, not to skip them");

        // TENANT-track candidates: db.<tenant-table> accesses with no AccountId
        // COMPARISON in the enclosing statement (the predicate rule applies).
        // Review P1-3: the filter is `!= true`, not `== false`, so a site the
        // scanner cannot classify (PredicateHasAccountId == null, "flag for
        // review") is a candidate too — it must be classified in the TSV rather
        // than silently passing. A `Select(u => u.AccountId)` projection no
        // longer reads as a predicate (HasAccountIdComparison returns false), so
        // a cross-tenant by-email enumeration is a candidate, not a pass.
        var tenantCandidates = GuardScanner.ScanFilterFreeSet(SrcRoot(), tenantNames)
            .Where(o => o.PredicateHasAccountId != true)
            .ToList();

        // NON-TENANT-track candidates: EVERY db.<non-tenant-set> access. These
        // sets have no AccountId column, so the predicate rule cannot apply —
        // but any query against a filter-free set is a bypass occurrence that
        // must be classified (scoped-by-join / scoped-by-user-id / global-
        // reference / non-tenant-sweep). An unclassified site is a red build.
        var nonTenantCandidates = GuardScanner.ScanFilterFreeSet(SrcRoot(), nonTenantNames).ToList();

        // #632 — keyed by IDENTITY (enclosing symbol + set + normalized-query
        // signature), never by file:line. Line identity moved three times on
        // edits that did not touch a query (#601, #609/#606, #627); #604 set
        // the threshold at three. The file:line survives only in the failure
        // text below, where it is how a human finds the code.
        var candidates = tenantCandidates.Concat(nonTenantCandidates)
            .OrderBy(GuardScanner.FilterFreeSetIdentity, StringComparer.Ordinal)
            .ToList();
        var located = candidates
            .GroupBy(GuardScanner.FilterFreeSetIdentity, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var candidateKeys = located.Keys.ToHashSet(StringComparer.Ordinal);

        // Two sites that key alike would let ONE classification excuse BOTH,
        // which is the failure mode a stable identity introduces and a line
        // number could not have. The signature's in-statement ordinal exists
        // for the only case where the text cannot differ; anything else
        // reaching here is a scheme bug, and it fails loudly rather than
        // quietly excusing a query nobody classified.
        var duplicateIdentities = candidates
            .GroupBy(GuardScanner.FilterFreeSetIdentity, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} — {string.Join(", ", g.Select(o => $"{o.File}:{o.Line}"))}")
            .ToList();
        Assert.True(duplicateIdentities.Count == 0,
            "two filter-free-set sites share one identity, so a single classification would excuse both " +
            "(#632). Report this as a scanner bug — the signature is supposed to separate them:\n  " +
            string.Join("\n  ", duplicateIdentities));

        // Load the classifications.
        var tsvPath = Path.Combine(AppContext.BaseDirectory, "TenantBypass", "Data", "filter-free-set-sites.tsv");
        // symbol <TAB> db.<Set> <TAB> signature <TAB> reason
        var classified = File.ReadAllLines(tsvPath)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"))
            .Select(l =>
            {
                var parts = l.Split('\t');
                Assert.True(parts.Length >= 4,
                    "malformed classification row — expected symbol, set, signature and reason separated by " +
                    $"tabs (#632):\n  {l}");
                return (Key: string.Join("\t", parts[0], parts[1], parts[2]), Reason: parts[3].Trim());
            })
            .ToList();

        var classifiedKeys = classified.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

        // A registry that excuses the same identity twice is a merge accident;
        // it also makes the stale check below unable to see the second one.
        var duplicateRows = classified.GroupBy(c => c.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicateRows.Count == 0,
            "duplicate classification rows in Data/filter-free-set-sites.tsv — one identity, one row (#632):\n  " +
            string.Join("\n  ", duplicateRows));

        // 1. Every candidate (both tracks) must be classified (no needs-review,
        // no missing). A new db.<tenant-table> query with no AccountId compare,
        // OR a new db.<non-tenant-set> query of any shape, appears as red here.
        // The failure text carries the identity to PASTE and the file:line to
        // READ. Only the first is the key; a line shift alone can no longer
        // land anything here (#632).
        var unclassified = candidates
            .Select(GuardScanner.FilterFreeSetIdentity)
            .Distinct(StringComparer.Ordinal)
            .Where(k => !classifiedKeys.Contains(k))
            .Select(k => $"{k}\n      at {located[k].File}:{located[k].Line}")
            .ToList();
        Assert.True(unclassified.Count == 0,
            "unclassified filter-free-set candidates (a new db.<tenant-table> query with no " +
            "AccountId compare, a new db.<non-tenant-set> query, or an EDITED query — a moved one no longer " +
            "reaches here). Paste each identity into Data/filter-free-set-sites.tsv with a reason " +
            "(scoped-by-X or needs-review), or fix the query:\n  " +
            string.Join("\n  ", unclassified));

        // 2. No classified site may have disappeared (drift).
        var stale = classifiedKeys.Except(candidateKeys, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "stale filter-free-set classifications (the query was deleted, renamed out of its method, or " +
            "edited — update Data/filter-free-set-sites.tsv):\n  " +
            string.Join("\n  ", stale));

        // 3. No needs-review sentinel may remain.
        var needsReview = classified.Where(c => c.Reason.StartsWith("needs-review", StringComparison.Ordinal)).ToList();
        Assert.True(needsReview.Count == 0,
            "filter-free-set sites still marked needs-review — classify or fix:\n  " +
            string.Join("\n  ", needsReview.Select(c => c.Key)));
    }

    // Completeness cross-check against the graphify knowledge graph (#583). The
    // Roslyn walk's filter-free-set surface should be a SUBSET of what the graph
    // models once #583 lands; until then the graph lacks db.<DbSet> call-site
    // links, so this is a soft assertion: it records the gap and fails only if
    // the graph regresses to modeling FEWER db.<tenant-table> sites than the
    // walk finds. Kept as a [SkippableFact] placeholder — activated when #583
    // ships the code↔table linkage. (No-op for now: the graph has no such links,
    // so there is nothing to compare; the assertion is documented, not run.)
    [Fact]
    public void GraphCompletenessCrossCheck_DocumentedNotYetActive()
    {
        // #583: graphify does not yet model db.<DbSet> query call-sites or
        // raw-SQL→table links. When it does, this test should assert:
        //   every (file, line) in the Roslyn walk's filter-free-set + raw-SQL
        //   occurrences is present in the graph's code↔table edges.
        // Until then it is a no-op guard so the cross-check's intent is
        // recorded in the suite and discoverable.
        Assert.True(true, "#583 not yet shipped — graph completeness cross-check is documented, not enforced.");
    }
}
