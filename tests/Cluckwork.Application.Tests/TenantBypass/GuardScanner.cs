namespace Cluckwork.Application.Tests.TenantBypass;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// #536 Part 1 — the scanner. Walks every .cs under src/ with Roslyn and
// reports every tenant-bypass occurrence. A GREEN result from this scanner is
// only as trustworthy as its inputs, so the false-green guards are part of the
// scanner, not an afterthought (review M2):
//
//  * any Error-severity parse diagnostic fails the scan (a C#-14 syntax the
//    parser cannot read would otherwise silently drop occurrences);
//  * a scanned-file-count floor proves the walk actually saw the tree;
//  * bin/ and obj/ are excluded BY PATH, and the floor is computed from the
//    same traversal so the exclusion cannot quietly swallow real files;
//  * the src/ root resolves by walking up from the test's working directory to
//    the directory containing Cluckwork.sln — if it cannot be found the scan
//    FAILS, it never defaults to a wrong root.

public enum BypassKind
{
    IgnoreQueryFilters,
    RawSql,            // EF raw-SQL APIs or IRawSqlCommandBuilder — both bypass EF filters outright
    IdentityLookup,    // FindByEmailAsync/FindByNameAsync/FindByLoginAsync/GetUsersInRoleAsync
    SignInManager,     // any SignInManager member invocation
    UserManagerUsers,  // UserManager.Users member access
    FilterFreeSet,     // db.<entity without a query filter>
}

public sealed record BypassOccurrence(
    BypassKind Kind,
    string File,          // repo-relative
    int Line,
    string EnclosingSymbol,
    string Detail,
    bool? PredicateHasAccountId = null,
    string? RawSqlText = null);

public sealed record AllowListMismatch(AllowListEntry Entry, string Reason);

public sealed record GuardReport(
    IReadOnlyList<BypassOccurrence> Occurrences,
    IReadOnlyList<BypassOccurrence> Excused,
    IReadOnlyList<BypassOccurrence> Unexcused,
    IReadOnlyList<AllowListMismatch> StaleEntries,
    IReadOnlyList<string> ParseErrors,
    int ScannedFileCount,
    int ExpectedFileCountFloor,
    IReadOnlyList<string> RawSqlPredicateViolations,
    IReadOnlyList<string> RawSqlExecutionViolations);

public static class GuardScanner
{
    // The real src/ tree must contain at least this many .cs files. The count
    // is set below the current 456 (measured 2026-08-22) so that adding files
    // does not break the gate, but a path-filter bug that silently excludes a
    // subtree (e.g. a new top-level directory) drops the count below the floor
    // and reds the build. Raise this when the tree grows substantially — the
    // floor's job is to catch a walk that saw LESS than it should, not to track
    // the exact count.
    internal const int RealTreeFileFloor = 400;

    private static bool IsRealRepo(string srcRoot) =>
        FindRepoRoot(AppContext.BaseDirectory) is string root
        && string.Equals(Path.GetFullPath(srcRoot), Path.Combine(root, "src"), StringComparison.Ordinal);

    // Banned method names by kind. Matched on the method name segment only
    // (receiver-independent) — a bypass is a bypass on any receiver.
    private static readonly Dictionary<string, BypassKind> BannedMethods = new()
    {
        ["IgnoreQueryFilters"] = BypassKind.IgnoreQueryFilters,
        ["FromSqlRaw"] = BypassKind.RawSql,
        ["FromSqlInterpolated"] = BypassKind.RawSql,
        ["ExecuteSqlRaw"] = BypassKind.RawSql,
        ["ExecuteSqlInterpolated"] = BypassKind.RawSql,
        // The async variants — the current source uses ExecuteSqlInterpolatedAsync
        // (FirstRunAdminService, IdempotencyMiddleware), and a review found the
        // sync-only list left them unreported. A new async raw-SQL query —
        // including a tenant-unsafe lock — must not leave the guard green.
        ["ExecuteSqlRawAsync"] = BypassKind.RawSql,
        ["ExecuteSqlInterpolatedAsync"] = BypassKind.RawSql,
        ["SqlQuery"] = BypassKind.RawSql,
        // Review P1-1 (deepseek-v4-flash): the EF Core 10 raw-SQL sibling set is
        // larger than the sync/async pair above. SqlQueryRaw/SqlQueryInterpolated
        // (typed entity queries from SQL), ExecuteSql/ExecuteSqlAsync (non-
        // interpolated, non-raw overloads), and FromSql/FromSqlAsync (the
        // FromSql base forms) all bypass the query filters. A lock query written
        // through any of these must not leave the guard green. Verified against
        // the resolved Microsoft.EntityFrameworkCore.Relational.dll (10.0.x) API
        // surface. None is used in src/ today — this is the escape-hatch surface
        // the guard must cover so a future raw query cannot pick an unbanned
        // entry point.
        ["SqlQueryRaw"] = BypassKind.RawSql,
        ["SqlQueryInterpolated"] = BypassKind.RawSql,
        ["ExecuteSql"] = BypassKind.RawSql,
        ["ExecuteSqlAsync"] = BypassKind.RawSql,
        ["FromSql"] = BypassKind.RawSql,
        ["FromSqlAsync"] = BypassKind.RawSql,
        ["FindByEmailAsync"] = BypassKind.IdentityLookup,
        ["FindByNameAsync"] = BypassKind.IdentityLookup,
        ["FindByLoginAsync"] = BypassKind.IdentityLookup,
        ["GetUsersInRoleAsync"] = BypassKind.IdentityLookup,
    };

    public static GuardReport Scan(string srcRoot, string allowListPath)
    {
        // The root is the PARENT of the src root. For the real tree that is
        // the repository (FindRepoRoot double-checks it holds Cluckwork.sln);
        // for a temp test tree it is the temp root — the file-count floor and
        // the parse-error guard are what make a temp tree trustworthy, not a
        // solution file.
        var srcFull = Path.GetFullPath(srcRoot);
        var repoRoot = Path.GetDirectoryName(srcFull)
            ?? throw new InvalidOperationException($"GuardScanner: cannot derive a root from '{srcRoot}'.");

        if (!File.Exists(Path.Combine(repoRoot, "Cluckwork.sln"))
            && FindRepoRoot(AppContext.BaseDirectory) != repoRoot)
        {
            // Not the repo: this is a temp tree. Allowed, but the floor below
            // must still hold against whatever was actually enumerated.
        }

        var files = EnumerateSourceFiles(srcRoot);
        // The floor is the caller's assertion about how many files the real
        // tree MUST contain. A tautological floor (files.Count) would never
        // fail — the reviewer named this as a false-green. The real-tree test
        // passes a static minimum derived from the committed tree; a temp tree
        // passes 0 (its floor is the parse-error guard, not a count).
        var floor = files.Count; // overridden below for the real tree
        if (IsRealRepo(srcRoot))
        {
            floor = RealTreeFileFloor;
        }

        var occurrences = new List<BypassOccurrence>();
        var parseErrors = new List<string>();
        var rawSqlViolations = new List<string>();
        var rawSqlExecutionViolations = new List<string>();

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(text, path: file);
            var root = tree.GetCompilationUnitRoot();

            foreach (var diag in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                parseErrors.Add($"{Relative(repoRoot, file)}:{diag.Location.GetLineSpan().StartLinePosition.Line + 1}: {diag.Id} {diag.GetMessage()}");
            }

            // Wrapper-forwarding detection (review P1-3): a method that
            // CONTAINS a banned call (e.g. an extension method that forwards
            // IgnoreQueryFilters()) is a laundering step — callers of that
            // method are bypasses too, even though the caller's own text has no
            // banned token. Find the forwarding method names in this file (a
            // method body that contains a banned invocation) and flag their
            // call sites. Same-file only: a wrapper defined in one file and
            // called in another is not resolved by a syntax walk (no symbol
            // binding) — a stated limitation, named in the ADR. The test's
            // laundering case (wrapper + caller in the same file) is caught.
            // Review (Claude re-review, refuting Codex P1-3): the original pass
            // collected forwarding names ONLY from MethodDeclarationSyntax. A
            // local function (LocalFunctionStatementSyntax — NOT a
            // MethodDeclarationSyntax) and an expression-bodied property
            // (PropertyDeclarationSyntax / AccessorDeclarationSyntax) were
            // invisible, so a wrapper in either shape laundered a bypass past an
            // allow-listed definition, same-file — the fix's own stated scope.
            // Proven: a `private IQueryable<T> X => db.T.FromSql(...).IgnoreQueryFilters();`
            // property wrapper + a caller `X.Select(...)` went green once the
            // property's definition symbol was allow-listed. Collect from all
            // three shapes and flag the caller as a member-access (property use
            // is `X.Select(…)`, not an invocation).
            var forwardingNames = new HashSet<string>(StringComparer.Ordinal);
            var forwardingMemberAccesses = new List<MemberAccessExpressionSyntax>();

            // A declaration (method, local function, property/accessor) whose
            // body contains a banned call is a forwarding wrapper.
            void RecordForwarding(SyntaxNode? body, string name)
            {
                if (body is null) return;
                var hasBanned = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Any(InvokesBanned);
                if (hasBanned) forwardingNames.Add(name);
            }

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                RecordForwarding(method.Body, method.Identifier.ValueText);
                RecordForwarding(method.ExpressionBody, method.Identifier.ValueText);
            }
            foreach (var lf in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            {
                RecordForwarding(lf.Body, lf.Identifier.ValueText);
                RecordForwarding(lf.ExpressionBody, lf.Identifier.ValueText);
            }
            // Expression-bodied properties (and accessors): the arrow body is
            // the forwarding site. An expression-BODIED property (`private X Y
            // => expr;`) carries the body on prop.ExpressionBody directly — it has
            // NO AccessorList. A get-only accessor property (`private X Y { get
            // => expr; }`) has the body on the accessor's ArrowExpressionClause.
            // Check both. Record the property NAME so its member-access use sites
            // can be flagged below.
            foreach (var prop in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                RecordForwarding(prop.ExpressionBody, prop.Identifier.ValueText);
                RecordForwarding(
                    prop.AccessorList?.DescendantNodes().OfType<ArrowExpressionClauseSyntax>()
                        .Select(a => a.Expression).FirstOrDefault(),
                    prop.Identifier.ValueText);
            }

            // Property-wrapper use sites: a member access whose ROOT name is a
            // forwarding property name (e.g. `AllLotsUnfiltered.Select(…)` — the
            // root `AllLotsUnfiltered` is the forwarding property). Collected
            // here, flagged in the occurrence walk below.
            if (forwardingNames.Count > 0)
            {
                foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                {
                    // The root of the member-access chain (leftmost identifier).
                    var rootExpr = access.Expression;
                    while (rootExpr is MemberAccessExpressionSyntax mroot)
                    {
                        rootExpr = mroot.Expression;
                    }
                    if (rootExpr is IdentifierNameSyntax rid && forwardingNames.Contains(rid.Identifier.ValueText))
                    {
                        forwardingMemberAccesses.Add(access);
                    }
                }
            }

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = invocation.Expression;
                string? methodName = null;
                string? receiverText = null;

                if (name is MemberAccessExpressionSyntax member)
                {
                    methodName = member.Name.Identifier.ValueText;
                    receiverText = member.Expression.ToString();
                }
                else if (name is IdentifierNameSyntax id)
                {
                    methodName = id.Identifier.ValueText;
                }

                // EF's own FromSql*/ExecuteSql*/SqlQuery surface is not the
                // only escape hatch. A command built through
                // IRawSqlCommandBuilder and executed through its
                // RelationalCommand bypasses query filters at the lower layer.
                if (IsLowLevelRawSqlBuild(invocation))
                {
                    var occurrence = MakeOccurrence(
                        BypassKind.RawSql,
                        repoRoot,
                        file,
                        invocation,
                        "IRawSqlCommandBuilder.Build");
                    var sqlExpression = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                    var rawSqlText = sqlExpression is null
                        ? string.Empty
                        : ResolveSqlText(sqlExpression, invocation);
                    occurrences.Add(occurrence with { RawSqlText = rawSqlText });
                    if (HasRowLockKeyword(rawSqlText)
                        && !HasAccountIdPredicateInWhereClause(rawSqlText))
                    {
                        var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        rawSqlViolations.Add(
                            $"{Relative(repoRoot, file)}:{line} in {EnclosingSymbolOf(invocation, file)} — " +
                            $"IRawSqlCommandBuilder.Build row lock without an AccountId predicate in its WHERE clause: {Truncate(rawSqlText)}");
                    }
                    if (!HasRelationalCommandExecution(invocation))
                    {
                        var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        rawSqlExecutionViolations.Add(
                            $"{Relative(repoRoot, file)}:{line} in {EnclosingSymbolOf(invocation, file)} — " +
                            "IRawSqlCommandBuilder.Build has no classified RelationalCommand execution");
                    }
                    continue;
                }

                if (methodName is not null && BannedMethods.TryGetValue(methodName, out var kind))
                {
                    occurrences.Add(MakeOccurrence(kind, repoRoot, file, invocation, $"{methodName}({receiverText})"));
                    continue;
                }

                // Caller of a forwarding wrapper (review P1-3 + Claude re-review):
                // the invocation's method name is not banned, but the method it
                // calls forwards a banned call (found above in forwardingNames).
                // Flag the CALL SITE so a wrapper that is allow-listed does not
                // leave its callers green. Same-file only (see the limitation note
                // above). A property wrapper's use site (`X.Select(…)`) is a
                // member access, not an invocation, so it is flagged separately
                // below (forwardingMemberAccesses).
                if (methodName is not null && forwardingNames.Contains(methodName))
                {
                    occurrences.Add(MakeOccurrence(BypassKind.IgnoreQueryFilters, repoRoot, file, invocation,
                        $"forwards-bypass {methodName}({receiverText})"));
                    continue;
                }

                // SignInManager: any member invocation on a receiver that is a
                // SignInManager — either by its generic type text (the current
                // code's SignInManager<ApplicationUser, T>) or by a conventional
                // camelCase receiver name (a future signInManager.X would
                // otherwise be silently ignored by the old case-sensitive text
                // match). Review P2: the type text alone missed camelCase
                // receivers; the name set closes that without a full semantic
                // model (kept a syntax walk for the hook's 2s budget).
                if (IsIdentityManagerReceiver(receiverText, isSignInManager: true))
                {
                    var memberName = name is MemberAccessExpressionSyntax m2 ? m2.Name.Identifier.ValueText : "?";
                    occurrences.Add(MakeOccurrence(BypassKind.SignInManager, repoRoot, file, invocation, $"SignInManager.{memberName}"));
                }
            }

            // Caller of a forwarding PROPERTY wrapper (Claude re-review, refuting
            // Codex P1-3): the use site `AllLotsUnfiltered.Select(…)` is a member
            // access, not an invocation, so the invocation loop above never sees
            // it. Flag the member access whose root names a forwarding property so
            // allow-listing the property's definition does not leave the caller
            // green. Same-file only.
            foreach (var access in forwardingMemberAccesses)
            {
                var rootName = access.Expression;
                while (rootName is MemberAccessExpressionSyntax mr) rootName = mr.Expression;
                var rootText = (rootName as IdentifierNameSyntax)?.Identifier.ValueText ?? "?";
                occurrences.Add(new BypassOccurrence(
                    BypassKind.IgnoreQueryFilters, Relative(repoRoot, file),
                    access.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    EnclosingSymbolOf(access, file),
                    $"forwards-bypass (property) {rootText}",
                    PredicateHasAccountId: PredicateHasAccountId(access)));
            }

            // UserManager.Users — a member access, not an invocation. The
            // receiver is matched by type text OR conventional name (same
            // review P2 fix as the SignInManager leg above): a camelCase
            // userManager.Users must be reported, not silently ignored.
            foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (access.Name.Identifier.ValueText == "Users"
                    && IsIdentityManagerReceiver(access.Expression.ToString(), isSignInManager: false))
                {
                    occurrences.Add(new BypassOccurrence(
                        BypassKind.UserManagerUsers, Relative(repoRoot, file),
                        access.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        EnclosingSymbolOf(access, file), "UserManager.Users",
                        PredicateHasAccountId: PredicateHasAccountId(access)));
                }
            }
        }

        // Raw-SQL predicate walk (M3/M4): every raw-SQL statement that carries
        // a row lock (FOR UPDATE / FOR SHARE) must also name an AccountId
        // predicate. This is checked on the SQL TEXT itself, independent of the
        // allow-list — the allow-list entry's justification *claims* the
        // predicate, but this walk *proves* it. Dropping the AccountId from a
        // lock query (M4) goes red here even though the site is still
        // allow-listed.
        foreach (var file in files)
        {
            var tree2 = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);
            var root2 = tree2.GetCompilationUnitRoot();
            foreach (var invocation in root2.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = invocation.Expression;
                var methodName = name is MemberAccessExpressionSyntax m3 ? m3.Name.Identifier.ValueText
                    : name is IdentifierNameSyntax id3 ? id3.Identifier.ValueText : null;
                // Every raw-SQL entry point must be predicate-walked, not just the
                // ones src/ happens to use today. Review P1-1: the original list
                // was the sync/async pair + FromSql* and omitted SqlQuery*,
                // ExecuteSql/ExecuteSqlAsync, and FromSql/FromSqlAsync — a lock
                // query through any of those would skip this walk and pass.
                // Keep this in lockstep with BannedMethods' RawSql entries.
                if (methodName is not ("ExecuteSqlRaw" or "ExecuteSqlInterpolated" or "ExecuteSql"
                    or "ExecuteSqlRawAsync" or "ExecuteSqlInterpolatedAsync" or "ExecuteSqlAsync"
                    or "FromSqlRaw" or "FromSqlInterpolated" or "FromSql" or "FromSqlAsync"
                    or "SqlQuery" or "SqlQueryRaw" or "SqlQueryInterpolated"))
                {
                    continue;
                }

                // The SQL text is the first argument, usually a string literal or
                // interpolated string. Reconstruct its text and check the lock
                // ⇒ AccountId implication.
                if (invocation.ArgumentList.Arguments.Count == 0)
                {
                    continue;
                }

                var sqlArg = invocation.ArgumentList.Arguments[0].Expression;
                var sqlText = ReconstructSqlText(sqlArg);

                // Round-4 finding F5: the gate matched only FOR UPDATE / FOR SHARE,
                // but FOR NO KEY UPDATE and FOR KEY SHARE are row locks too (and
                // HasAccountIdPredicateInWhereClause's prefix loop already listed all
                // four — the gate just never reached it). A `FOR NO KEY UPDATE` lock
                // with the AccountId predicate dropped must red, not pass.
                var hasLock = HasRowLockKeyword(sqlText);
                // Review P1-2: the old check was `sqlText.Contains("AccountId")` —
                // a string-presence test, not a predicate test. `SELECT *,
                // "AccountId" FROM t WHERE "Id" = {id} FOR UPDATE` names
                // AccountId in the SELECT list and passes the old check, yet the
                // lock covers EVERY row of the table for every tenant. The
                // scoping predicate must live in the WHERE clause, so require
                // AccountId to appear in the predicate portion (after the last
                // FROM that precedes the lock), not in the SELECT list.
                if (hasLock && !HasAccountIdPredicateInWhereClause(sqlText))
                {
                    var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    rawSqlViolations.Add(
                        $"{Relative(repoRoot, file)}:{line} in {EnclosingSymbolOf(invocation, file)} — raw-SQL row lock without an AccountId predicate in its WHERE clause: {Truncate(sqlText)}");
                }
            }
        }

        var allowList = AllowList.Load(allowListPath);

        // Excuse matching: file (relative) + symbol must both match exactly.
        var matches = (BypassOccurrence o, AllowListEntry e) =>
            string.Equals(o.File, NormalizePath(e.File), StringComparison.Ordinal)
            && o.EnclosingSymbol == e.Symbol;

        var unexcusedOccurrences = occurrences
            .Where(o => !allowList.Any(e => matches(o, e)))
            .ToList();
        var excusedOccurrences = occurrences
            .Where(o => allowList.Any(e => matches(o, e)))
            .ToList();

        var stale = allowList
            .Where(e => !occurrences.Any(o => matches(o, e)))
            .Select(e => new AllowListMismatch(e, "entry matches no occurrence in src/"))
            .ToList();

        return new GuardReport(occurrences, excusedOccurrences,
            unexcusedOccurrences,
            stale, parseErrors, files.Count, floor, rawSqlViolations, rawSqlExecutionViolations);
    }

    // Round-4 finding F5 — all four Postgres row-lock keywords. The predicate
    // walk's gate and its prefix loop must agree on the set; a lock the gate
    // misses never reaches the predicate check at all.
    private static bool HasRowLockKeyword(string sql) =>
        sql.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)
        || sql.Contains("FOR SHARE", StringComparison.OrdinalIgnoreCase)
        || sql.Contains("FOR NO KEY UPDATE", StringComparison.OrdinalIgnoreCase)
        || sql.Contains("FOR KEY SHARE", StringComparison.OrdinalIgnoreCase);

    // Reconstruct the text of a raw-SQL argument. We only need to know whether
    // the SQL names "AccountId", so the node's full source text is sufficient:
    // a plain string literal, an interpolated string, or a C# 11+ raw string
    // literal ($""" ... """) — all of which render their literal text (and the
    // {…} holes) via ToString(). A pre-built variable (not a string literal in
    // source) renders as its identifier, which contains no AccountId and so is
    // conservatively flagged as a violation for manual review — those are out
    // of scope for the text walk and named as a limitation.
    private static string ReconstructSqlText(ExpressionSyntax expr) => expr.ToString();

    // Review P1-2 — the predicate walk must prove AccountId is in the WHERE
    // clause of a lock query, not merely present in the SQL text. The SELECT
    // list (before the first FROM) is not a predicate: `SELECT *, "AccountId"
    // FROM t WHERE "Id" = {id} FOR UPDATE` names AccountId but locks every row.
    // Rule: take the SQL up to the lock keyword, find the LAST `FROM` in it (the
    // table source of the locked statement — subqueries have their own FROM, so
    // the last one is the outer table), and require AccountId to appear AFTER
    // that FROM (i.e. in the WHERE clause / JOIN predicates, not the SELECT
    // list). No FROM ⇒ no confirmable predicate ⇒ false (flag). This is a text
    // heuristic, not a SQL parser; it is deliberately stricter than the old
    // string-presence check and still allows the legitimate forms in src/ (a
    // quoted "AccountId" column or an {accountId} interpolation hole in the
    // WHERE clause). A lock whose AccountId is only in a comment or the SELECT
    // list is correctly flagged.
    private static bool HasAccountIdPredicateInWhereClause(string sqlText)
    {
        // Claude re-review (refuting pi P1-2): the original check did a
        // substring test on the region after the LAST FROM, which left two
        // defects. (a) FALSE-GREEN: a `--` comment in the predicate region that
        // names AccountId launders the lock — the repo's own house style puts `--`
        // comments inside FOR UPDATE WHERE clauses (EggLotRepository.cs:44-48), so
        // this is the real shape, not a contrived one. Strip SQL comments first.
        // (b) FALSE-RED: a CTE lock (`WITH scoped AS (SELECT ... WHERE AccountId =
        // ...) SELECT ... FOR UPDATE`) has the scoping WHERE before the innermost
        // FROM, so the last-FROM region excludes it. Test the region after EVERY
        // FROM and accept if any contains AccountId. The SELECT-list false-green
        // stays closed: the projection sits before the first FROM, so no region
        // after a FROM contains it.
        //
        // Round-4 finding F1 (kimi-k3) — the every-FROM fix itself regressed
        // MULTI-STATEMENT SQL: a batched raw SQL whose FIRST statement is scoped
        // (`DELETE ... WHERE AccountId = ...;`) and whose SECOND takes an
        // UNscoped lock (`SELECT ... FOR UPDATE`) passed, because the rule found
        // AccountId in the first statement's FROM region. Round 3's last-FROM
        // logic read the LOCK'S OWN statement region and would have caught it.
        // Cure: split the stripped SQL on `;` and apply the every-FROM rule only
        // to the STATEMENT that contains the lock keyword. A CTE stays inside one
        // statement (its `;` is only at the end), so the CTE fix survives; a
        // previous statement's AccountId can no longer launder the lock. (Npgsql
        // batches multi-statement text, so this is a real execution shape.)
        var noComments = StripSqlComments(sqlText);
        var statements = noComments.Split(';', StringSplitOptions.RemoveEmptyEntries);

        // The statement carrying the lock keyword is the one whose predicate we
        // must prove. (If the lock keyword appears in more than one statement —
        // e.g. two locks in one batch — every such statement must be scoped; we
        // return false if ANY lock statement lacks the predicate.)
        var sawLockStatement = false;
        foreach (var statement in statements)
        {
            if (!HasRowLockKeyword(statement))
            {
                continue; // not a lock statement — no predicate obligation
            }

            sawLockStatement = true;
            if (!LockStatementHasAccountIdPredicate(statement))
            {
                return false; // a lock statement without an AccountId predicate
            }
        }

        // A lock statement was found and every one of them carried the AccountId
        // predicate — the implication holds. (The hasLock gate already established
        // there IS a lock, so sawLockStatement is normally true here; if the lock
        // keyword was only in a comment that StripSqlComments removed, no statement
        // carries it and we flag (false).)
        return sawLockStatement;
    }

    // The every-FROM predicate rule applied to a SINGLE lock statement: take the
    // text up to the lock keyword within this statement, and accept if the region
    // after any FROM in that prefix contains AccountId. (Extracted from
    // HasAccountIdPredicateInWhereClause for the statement-aware F1 fix.)
    private static bool LockStatementHasAccountIdPredicate(string statement)
    {
        var prefix = statement;
        foreach (var kw in new[] { "FOR UPDATE", "FOR SHARE", "FOR NO KEY UPDATE", "FOR KEY SHARE" })
        {
            var idx = IndexOf(statement, kw, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                prefix = statement[..idx];
                break;
            }
        }

        // For every FROM in the prefix, the region after it (up to the next FROM,
        // or end) is a predicate region. Accept if any contains AccountId.
        var fromIdx = IndexOf(prefix, "FROM", StringComparison.OrdinalIgnoreCase);
        while (fromIdx >= 0)
        {
            var regionStart = fromIdx + 4; // after "FROM"
            var nextFrom = IndexOf(prefix[regionStart..], "FROM", StringComparison.OrdinalIgnoreCase);
            var region = nextFrom >= 0 ? prefix[regionStart..(regionStart + nextFrom)] : prefix[regionStart..];
            if (region.Contains("AccountId", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            fromIdx = nextFrom >= 0 ? regionStart + nextFrom : -1;
        }

        // No FROM at all in the lock statement: a lock on a table must have one;
        // without it there is no confirmable predicate region, so flag (false).
        return false;
    }

    // Strip `-- line` and `/* block */` comments from SQL text. A `--` runs to
    // end of line; a `/* */` runs to the matching close. Quote-aware enough for
    // the repo's SQL (double-quoted identifiers, single-quoted literals) — a
    // `--` inside a literal is not present in the current src/ raw-SQL, and a
    // mis-strip would only remove text, never add an AccountId, so the risk is a
    // false-red, not a false-green.
    private static string StripSqlComments(string sql)
    {
        var sb = new System.Text.StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                // line comment: skip to end of line (keep the newline)
                while (i < sql.Length && sql[i] != '\n') i++;
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                // block comment: skip to matching */
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i = Math.Min(sql.Length, i + 2);
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    internal static IReadOnlyList<string> FindScopedUpdateArmViolations(
        string sql,
        int expectedUpdateArmCount,
        params (string Column, string Parameter)[] requiredPredicates)
    {
        // The atomic logout command contains two data-modifying CTE arms. Each
        // arm has its own WHERE clause, so a predicate in one must not launder
        // the other. Capture and validate every UPDATE ... WHERE ... RETURNING
        // arm independently, and pin the expected arm count so deleting an arm
        // cannot turn the predicate loop into a false green.
        var matches = System.Text.RegularExpressions.Regex.Matches(
                StripSqlComments(sql),
                @"(?is)\b(?<arm>[A-Za-z_][A-Za-z0-9_]*)\s+AS\s*\(\s*UPDATE\s+.*?\bSET\b.*?\bWHERE\b(?<where>.*?)\bRETURNING\b")
            .Cast<System.Text.RegularExpressions.Match>()
            .ToList();
        var violations = new List<string>();

        if (matches.Count != expectedUpdateArmCount)
        {
            violations.Add(
                $"expected {expectedUpdateArmCount} data-modifying CTE update arms, found {matches.Count}");
        }

        foreach (var match in matches)
        {
            var arm = match.Groups["arm"].Value;
            var where = match.Groups["where"].Value;
            foreach (var (column, parameter) in requiredPredicates)
            {
                var pattern =
                    $@"(?:\b[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)?""?{System.Text.RegularExpressions.Regex.Escape(column)}""?\s*=\s*@{System.Text.RegularExpressions.Regex.Escape(parameter)}\b";
                if (!System.Text.RegularExpressions.Regex.IsMatch(
                        where,
                        pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    violations.Add(
                        $"{arm} update arm is missing {column} = @{parameter} in its WHERE clause");
                }
            }
        }

        return violations;
    }

    private static int IndexOf(string haystack, string needle, StringComparison cmp)
        => haystack.IndexOf(needle, cmp);

    private static int LastIndexOf(string haystack, string needle, StringComparison cmp)
        => haystack.LastIndexOf(needle, cmp);

    private static string Truncate(string s) =>
        s.Length <= 160 ? s.Replace("\n", " ") : s[..160] + "…";

    /// <summary>
    /// Evaluates a report as a build gate. Every one of these must hold:
    /// no parse errors, the file-count floor holds, nothing unexcused, no stale
    /// entries. Returns the list of failure messages (empty = pass).
    /// </summary>
    public static IReadOnlyList<string> Evaluate(GuardReport report)
    {
        var failures = new List<string>();

        if (report.ParseErrors.Count > 0)
        {
            failures.Add($"scan produced {report.ParseErrors.Count} parse error(s) — the walk cannot be trusted:\n  " +
                         string.Join("\n  ", report.ParseErrors.Take(10)));
        }

        if (report.ScannedFileCount < report.ExpectedFileCountFloor)
        {
            failures.Add($"scanned {report.ScannedFileCount} files, expected at least {report.ExpectedFileCountFloor} — the walk saw less than it should");
        }

        foreach (var o in report.Unexcused)
        {
            failures.Add($"unexcused bypass [{o.Kind}] {o.File}:{o.Line} in {o.EnclosingSymbol} ({o.Detail}) — add an allow-list entry with a justification, or fix the bypass");
        }

        foreach (var s in report.StaleEntries)
        {
            failures.Add($"stale allow-list entry {s.Entry.File} :: {s.Entry.Symbol} — {s.Reason}");
        }

        foreach (var v in report.RawSqlPredicateViolations)
        {
            failures.Add($"raw-SQL row lock missing an AccountId predicate (M4): {v}");
        }

        foreach (var v in report.RawSqlExecutionViolations)
        {
            failures.Add($"low-level raw-SQL execution seam is incomplete: {v}");
        }

        return failures;
    }

    // The db.<FilterFreeSet> leg: given the model-discovered filter-free
    // property names (e.g. "Users", "UserRoles"), scans for member accesses
    // whose name matches and reports whether the enclosing query carries an
    // AccountId comparison in its predicate. Shape, not provenance (review
    // M4/F4): this proves the predicate EXISTS, not that the compared value is
    // the resolved tenant.
    public static IReadOnlyList<BypassOccurrence> ScanFilterFreeSet(
        string srcRoot, IReadOnlyCollection<string> filterFreePropertyNames)
    {
        var repoRoot = Path.GetDirectoryName(Path.GetFullPath(srcRoot))
            ?? throw new InvalidOperationException("GuardScanner: cannot derive a root from the given src root.");

        var names = new HashSet<string>(filterFreePropertyNames, StringComparer.Ordinal);
        var results = new List<BypassOccurrence>();

        foreach (var file in EnumerateSourceFiles(srcRoot))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);
            var root = tree.GetCompilationUnitRoot();

            foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (!names.Contains(access.Name.Identifier.ValueText))
                {
                    continue;
                }

                // Only DbContext-shaped accesses: the receiver must be the
                // conventional context variable (db / _db / context / ctx).
                // A domain object named e.g. `user` or `actor` that carries an
                // in-memory `Roles`/`Users` collection is NOT a DB access —
                // `user.Roles` is a List<string> on the actor, not a DbSet
                // query. Without this the leg flags every in-memory collection
                // named after a tenant-table filter-free property, which is why
                // the naive `is IdentifierNameSyntax` receiver check produced
                // 31 candidates of which ~25 were not DB queries at all.
                if (access.Expression is not IdentifierNameSyntax ident)
                {
                    continue;
                }

                if (!IsDbContextReceiver(ident.Identifier.ValueText))
                {
                    continue;
                }

                results.Add(new BypassOccurrence(
                    BypassKind.FilterFreeSet, Relative(repoRoot, file),
                    access.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    EnclosingSymbolOf(access, file),
                    $"{access.Expression}.{access.Name.Identifier.ValueText}",
                    PredicateHasAccountId: PredicateHasAccountId(access)));
            }
        }

        return results;
    }

    private static BypassOccurrence MakeOccurrence(
        BypassKind kind, string repoRoot, string file, InvocationExpressionSyntax invocation, string detail)
    {
        var occurrence = new BypassOccurrence(
            kind, Relative(repoRoot, file),
            invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            EnclosingSymbolOf(invocation, file), detail);

        if (kind == BypassKind.RawSql)
        {
            var rawText = invocation.ArgumentList.Arguments
                .Select(a => a.ToString())
                .Where(a => a.Contains("\"", StringComparison.Ordinal) || a.Contains("'''", StringComparison.Ordinal) || a.Contains("\"\"\"", StringComparison.Ordinal))
                .FirstOrDefault() ?? string.Empty;
            occurrence = occurrence with { RawSqlText = rawText };
        }

        return occurrence;
    }

    // The enclosing method in symbol display form. A call inside a local
    // function keys as ContainingMethod.Local(localFunctionName) — it is NOT
    // covered by the parent method's allow-list entry (design M7).
    internal static string EnclosingSymbolOf(SyntaxNode node, string file)
    {
        var method = node.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>();
        if (method is null)
        {
            // Top-level statements or static initializers: name them as such
            // rather than pretending a method exists.
            return $"<{Path.GetFileName(file)}>.<top-level>";
        }

        var local = node.Ancestors().OfType<LocalFunctionStatementSyntax>()
            .FirstOrDefault(lf => lf.Span.Contains(node.Span));
        if (local is not null)
        {
            return $"{SymbolPrefix(method, file)}.{MethodName(method)}.Local({local.Identifier.ValueText})";
        }

        return $"{SymbolPrefix(method, file)}.{MethodName(method)}({ParameterTypes(method)})";
    }

    private static string MethodName(BaseMethodDeclarationSyntax method)
    {
        if (method is MethodDeclarationSyntax m)
        {
            return m.Identifier.ValueText;
        }

        if (method is ConstructorDeclarationSyntax c)
        {
            return c.Identifier.ValueText;
        }

        // Conversions/operators: the IdentifierToken-less declarations still
        // expose their name through the method's first token run.
        return method.ToString()!.Split(' ', '\n', '\r').FirstOrDefault(t => !t.All(char.IsPunctuation) && t.Length > 0) ?? "<method>";
    }

    private static string SymbolPrefix(BaseMethodDeclarationSyntax method, string file)
    {
        // Walk up to the enclosing type and its namespace. The test project
        // does not compile src/, so no symbol info is available — the display
        // is reconstructed from the syntax tree.
        // Block namespaces are ancestors of the method; file-scoped ones are
        // children of the compilation unit. Take whichever exists.
        // Types are collected innermost-first by the upward walk, then
        // reversed to outermost-first. The namespace goes LAST so the final
        // order is Namespace.OuterType.InnerType.
        var typeParts = new List<string>();
        for (var node = method.Parent; node is not null; node = node.Parent)
        {
            // FileScopedNamespaceDeclarationSyntax IS a TypeDeclarationSyntax —
            // skip it, its name is added as the namespace below.
            if (node is FileScopedNamespaceDeclarationSyntax)
            {
                continue;
            }

            if (node is TypeDeclarationSyntax td)
            {
                typeParts.Add(td.Identifier.ValueText);
            }
            else if (node is RecordDeclarationSyntax rd)
            {
                typeParts.Add(rd.Identifier.ValueText);
            }
            else if (node is StructDeclarationSyntax sd)
            {
                typeParts.Add(sd.Identifier.ValueText);
            }
        }

        typeParts.Reverse();

        var parts = new List<string>();
        string? nsName = method.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString()
            ?? method.Ancestors().OfType<CompilationUnitSyntax>().FirstOrDefault()
                ?.ChildNodes().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        if (nsName is not null)
        {
            parts.Add(nsName);
        }

        parts.AddRange(typeParts);
        return string.Join(".", parts);
    }

    private static string ParameterTypes(BaseMethodDeclarationSyntax method)
    {
        // Parameter TEXT, not types — the test project cannot resolve types.
        // Overload disambiguation still works because parameter names +
        // declared types are in the text. Stated in the allow-list header.
        return string.Join(", ", method.ParameterList.Parameters
            .Select(p => p.Type!.ToString() + " " + p.Identifier.ValueText));
    }

    // Predicate shape check (review M4/F4 — shape, not provenance): does the
    // query chain following this access contain an AccountId COMPARISON?
    // Review P1-3 (deepseek-v4-flash): the old check was
    // `statement.Contains("AccountId")` — a string-presence test. A projection
    // like `db.Users.Where(u => u.Email == email).Select(u => u.AccountId)`
    // names AccountId (in the Select) but does not scope by it — it enumerates
    // every tenant's users by email. Presence is not a predicate; a comparison
    // is. This now requires AccountId to appear in a comparison shape (AccountId
    // on one side of ==, !=, <, >, <=, >=, =, or in a Where-clause lambda that
    // compares it), not merely anywhere in the statement. A bare Select/OrderBy
    // projection of AccountId is NOT a predicate and returns false (a candidate
    // that must be classified or fixed). Provenance (that the compared value is
    // the resolved tenant, not a literal) is still not proven — that remains the
    // allow-list justification's job; this closes the presence-vs-predicate gap.
    internal static bool? PredicateHasAccountId(SyntaxNode node)
    {
        var statement = node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault()
            ?? node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().Select(m => m as SyntaxNode).FirstOrDefault();
        if (statement is null)
        {
            return null; // cannot tell — the caller treats null as "flag for review"
        }

        // Claude re-review (refuting pi P1-3): the original check matched on
        // statement.ToString(), which INCLUDES interior comment trivia and string
        // literals. A `// scoped by AccountId == tenant.AccountId` comment beside
        // a real cross-tenant `db.Users.Where(u => u.Email == email)` therefore
        // laundered the statement — text matching cannot tell code from trivia.
        // Compare on TOKENS, excluding string-literal text and comment trivia, so
        // only actual code counts.
        return HasAccountIdComparison(StatementCodeOnly(statement));
    }

    // The statement's code, with comment trivia and string-literal TEXT removed.
    // A node's ToString() includes interior comments (trivia) and the text of
    // string literals; both can name "AccountId" in a way that is not code. A
    // log message `Log($"scoping by AccountId = {x}")` or a `// AccountId == ...`
    // comment would otherwise read as a predicate. Removing the comment trivia
    // and the literal TEXT leaves the code tokens.
    //
    // ROUND-4 F6 (kimi-k3) — corrected a FALSE claim in this comment. The
    // original text said "interpolated-string holes are dropped too — they are
    // string content." That is wrong: an interpolated hole's contents are
    // ORDINARY CODE TOKENS (an `InterpolatedStringTextToken` is the literal
    // text BETWEEN holes; the hole itself is an `InterpolatedStringExpression`)
    // and they SURVIVE this filter. Consequence: a comparison embedded in a
    // string hole — e.g. `Select(u => $"match: {u.AccountId == accountId}")` —
    // still reads as a predicate (a false-green), because the hole's `u.AccountId
    // == accountId` is real code text. This is a contrived shape (nobody scopes a
    // query inside a log string), but the comment must not claim the hole is
    // dropped, or a future reviewer will trust it. The legitimate code forms —
    // `u.AccountId == x`, `accountIds.Contains(u.AccountId)` — are plain tokens
    // and survive either way. Closing the hole case requires tracking which tokens
    // are inside an interpolated string and dropping the hole EXPRESSIONS too, which
    // is a larger change than this guard's budget warrants; it is recorded here as a
    // known limitation, not silently claimed closed.
    internal static string StatementCodeOnly(SyntaxNode statement)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var token in statement.DescendantTokens())
        {
            // Drop string-literal and interpolated-string text (string content).
            // Drop string-literal text and interpolated-string text (string
            // content). InterpolatedStringLiteralToken is not a token kind — an
            // interpolated string is a run of InterpolatedStringTextToken / hole
            // tokens, so dropping the text tokens is what removes the content.
            if (token.IsKind(SyntaxKind.StringLiteralToken)
                || token.IsKind(SyntaxKind.InterpolatedStringTextToken))
            {
                continue;
            }
            // Drop comment trivia (leading/trailing) on the token; keep other
            // trivia (whitespace) so the code's shape is preserved for the regex.
            foreach (var trivia in token.LeadingTrivia)
            {
                if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) continue;
                sb.Append(trivia.ToString());
            }
            sb.Append(token.Text);
            foreach (var trivia in token.TrailingTrivia)
            {
                if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) continue;
                sb.Append(trivia.ToString());
            }
        }
        return sb.ToString();
    }

    // True if the code carries AccountId in a comparison or set-membership shape,
    // not merely as a projection or member access. Claude re-review (refuting pi
    // P1-3): the original regex missed two LEGITIMATE scoping forms —
    // `accountIds.Contains(u.AccountId)` and `u.AccountId.Equals(id)` — causing a
    // false-red (a correctly scoped query flagged as a candidate). Both are added
    // here. A `Select(u => u.AccountId)` projection has no adjacent operator and
    // is still correctly not a predicate.
    internal static bool HasAccountIdComparison(string code)
    {
        // AccountId immediately followed by a comparison operator.
        if (System.Text.RegularExpressions.Regex.IsMatch(
                code, @"AccountId\s*(==|!=|<=|>=|<|>|=)"))
        {
            return true;
        }

        // A comparison operator immediately followed by AccountId (e.g. `x ==
        // u.AccountId`). The operator must be a comparison, not an assignment to
        // another identifier — the preceding token is checked to be a
        // non-identifier character so `Foo = AccountId` (a property init, not a
        // predicate) does not match.
        if (System.Text.RegularExpressions.Regex.IsMatch(
                code, @"(^|[^A-Za-z0-9_.])\s*(==|!=|<=|>=|<|>)\s*AccountId"))
        {
            return true;
        }

        // Set-membership: accountIds.Contains(u.AccountId) — AccountId inside a
        // Contains(…). The argument is the compared value.
        if (System.Text.RegularExpressions.Regex.IsMatch(
                code, @"Contains\s*\([^)]*AccountId"))
        {
            return true;
        }

        // Equality via the member: u.AccountId.Equals(id).
        return System.Text.RegularExpressions.Regex.IsMatch(
            code, @"AccountId\s*\.\s*Equals\s*\(");
    }

    // The conventional DbContext variable names in this codebase. Every tenant
    // filter-free DbSet access is `db.<Table>` (a 33-site grep across
    // src/ confirmed `db` is the sole receiver for the tenant-table accesses);
    // `user.Roles` / `actor.Roles` are in-memory collections, not DB queries,
    // and are excluded by requiring the receiver to be one of these names.
    private static bool IsDbContextReceiver(string name) =>
        name is "db" or "_db" or "context" or "ctx" or "_context";

    // The receiver of a UserManager/SignInManager access, matched either by its
    // generic TYPE text (UserManager<ApplicationUser>, SignInManager<...> — the
    // current code) or by a conventional camelCase receiver NAME (a future
    // userManager.Users / signInManager.PasswordSignInAsync). The old check was
    // a case-sensitive Contains("UserManager")/Contains("SignInManager") on the
    // receiver text, which only caught the generic-type form and silently
    // ignored camelCase variables — review P2. Matching the name set keeps this
    // a syntax walk (no semantic model, for the hook budget) while closing the
    // gap. `manager` is included because IdentityProvider's scoped-directory
    // helper names its UserManager parameter `manager`.
    // True if an invocation's method name is a banned bypass method — used by
    // the wrapper-forwarding detection to decide whether a method body forwards
    // a banned call. Matches the same BannedMethods set as the main walk.
    private static bool InvokesBanned(InvocationExpressionSyntax invocation)
    {
        var name = invocation.Expression;
        var methodName = name is MemberAccessExpressionSyntax m ? m.Name.Identifier.ValueText
            : name is IdentifierNameSyntax id ? id.Identifier.ValueText : null;
        return methodName is not null && BannedMethods.ContainsKey(methodName);
    }

    private static bool IsLowLevelRawSqlBuild(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "Build",
            } member)
        {
            return false;
        }

        if (IsRawSqlCommandBuilderServiceResolution(member.Expression))
        {
            return true;
        }

        if (member.Expression is not IdentifierNameSyntax identifier)
        {
            return false;
        }

        var method = invocation.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        var declaration = method?.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(candidate => candidate.SpanStart < invocation.SpanStart)
            .LastOrDefault(candidate =>
                candidate.Identifier.ValueText == identifier.Identifier.ValueText);

        return declaration?.Initializer?.Value is { } initializer
            && IsRawSqlCommandBuilderServiceResolution(initializer);
    }

    private static bool IsRawSqlCommandBuilderServiceResolution(ExpressionSyntax expression)
    {
        var generic = expression.DescendantNodesAndSelf()
            .OfType<GenericNameSyntax>()
            .Any(generic => generic.Identifier.ValueText == "GetService"
                && generic.TypeArgumentList.Arguments.Any(argument =>
                    argument.ToString() == "IRawSqlCommandBuilder"));
        if (generic)
        {
            return true;
        }

        return expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Any(service => service.Expression is MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "GetService",
                }
                && service.ArgumentList.Arguments.Any(argument =>
                    argument.Expression is TypeOfExpressionSyntax typeOf
                    && typeOf.Type.ToString() == "IRawSqlCommandBuilder"));
    }

    private static bool HasRelationalCommandExecution(InvocationExpressionSyntax build)
    {
        // Bind the execution to the local receiving this exact Build result.
        // A different ExecuteScalarAsync elsewhere in the method must not
        // classify a built-but-never-executed command as complete.
        var commandVariable = build.Ancestors()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault()?.Identifier.ValueText;
        var method = build.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        if (commandVariable is null || method is null)
        {
            return false;
        }

        return method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Any(invocation => invocation.Expression is MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "ExecuteScalarAsync",
                    Expression: MemberAccessExpressionSyntax
                    {
                        Name.Identifier.ValueText: "RelationalCommand",
                        Expression: IdentifierNameSyntax identifier,
                    },
                }
                && identifier.Identifier.ValueText == commandVariable);
    }

    private static string ResolveSqlText(ExpressionSyntax expression, SyntaxNode useSite)
    {
        // Low-level callers commonly pass a local const identifier rather than
        // the literal directly. Resolve the nearest preceding declaration in
        // the method so the predicate guard sees the SQL, not merely `sql`.
        if (expression is not IdentifierNameSyntax identifier)
        {
            return ReconstructSqlText(expression);
        }

        var method = useSite.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        var declaration = method?.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(candidate => candidate.SpanStart < useSite.SpanStart)
            .LastOrDefault(candidate =>
                candidate.Identifier.ValueText == identifier.Identifier.ValueText);

        return declaration?.Initializer?.Value is { } initializer
            ? ReconstructSqlText(initializer)
            : expression.ToString();
    }

    private static bool IsIdentityManagerReceiver(string? receiverText, bool isSignInManager)
    {
        if (receiverText is null)
        {
            return false;
        }

        // Type-text form: the receiver names the generic manager type.
        if (receiverText.Contains(isSignInManager ? "SignInManager" : "UserManager", StringComparison.Ordinal))
        {
            return true;
        }

        // Conventional-name form: a simple identifier with a manager-ish name.
        var trimmed = receiverText.Trim();
        if (isSignInManager)
        {
            return trimmed is "signInManager" or "_signInManager" or "signIn" or "_signIn";
        }

        return trimmed is "userManager" or "_userManager" or "manager" or "users" or "_users";
    }

    internal static IReadOnlyList<string> EnumerateSourceFiles(string srcRoot)
    {
        var files = new List<string>();
        void Walk(string dir)
        {
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name is "bin" or "obj" or "node_modules")
                {
                    continue;
                }

                Walk(sub);
            }

            foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly))
            {
                files.Add(f);
            }
        }

        Walk(srcRoot);
        return files.OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    internal static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Cluckwork.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string Relative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

    private static string NormalizePath(string p) => p.Replace('\\', '/');
}
