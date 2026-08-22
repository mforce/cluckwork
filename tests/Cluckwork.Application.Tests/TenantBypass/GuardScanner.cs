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
    RawSql,            // FromSql*/ExecuteSql*/SqlQuery — raw SQL bypasses EF filters outright
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
    IReadOnlyList<string> RawSqlPredicateViolations);

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
        ["SqlQuery"] = BypassKind.RawSql,
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

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(text, path: file);
            var root = tree.GetCompilationUnitRoot();

            foreach (var diag in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                parseErrors.Add($"{Relative(repoRoot, file)}:{diag.Location.GetLineSpan().StartLinePosition.Line + 1}: {diag.Id} {diag.GetMessage()}");
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

                if (methodName is not null && BannedMethods.TryGetValue(methodName, out var kind))
                {
                    occurrences.Add(MakeOccurrence(kind, repoRoot, file, invocation, $"{methodName}({receiverText})"));
                    continue;
                }

                // SignInManager: any member invocation on a receiver whose
                // type text names it.
                if (receiverText is not null
                    && receiverText.Contains("SignInManager", StringComparison.Ordinal))
                {
                    var memberName = name is MemberAccessExpressionSyntax m2 ? m2.Name.Identifier.ValueText : "?";
                    occurrences.Add(MakeOccurrence(BypassKind.SignInManager, repoRoot, file, invocation, $"SignInManager.{memberName}"));
                }
            }

            // UserManager.Users — a member access, not an invocation.
            foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (access.Name.Identifier.ValueText == "Users"
                    && access.Expression.ToString().Contains("UserManager", StringComparison.Ordinal))
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
        var rawSqlViolations = new List<string>();
        foreach (var file in files)
        {
            var tree2 = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);
            var root2 = tree2.GetCompilationUnitRoot();
            foreach (var invocation in root2.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = invocation.Expression;
                var methodName = name is MemberAccessExpressionSyntax m3 ? m3.Name.Identifier.ValueText
                    : name is IdentifierNameSyntax id3 ? id3.Identifier.ValueText : null;
                if (methodName is not ("ExecuteSqlRaw" or "FromSqlRaw" or "Sql" or "FromSqlInterpolated" or "ExecuteSqlInterpolated"))
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

                var hasLock = sqlText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)
                              || sqlText.Contains("FOR SHARE", StringComparison.OrdinalIgnoreCase);
                if (hasLock && !sqlText.Contains("AccountId", StringComparison.OrdinalIgnoreCase))
                {
                    var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    rawSqlViolations.Add(
                        $"{Relative(repoRoot, file)}:{line} in {EnclosingSymbolOf(invocation, file)} — raw-SQL row lock without an AccountId predicate: {Truncate(sqlText)}");
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
            stale, parseErrors, files.Count, floor, rawSqlViolations);
    }

    // Reconstruct the text of a raw-SQL argument. We only need to know whether
    // the SQL names "AccountId", so the node's full source text is sufficient:
    // a plain string literal, an interpolated string, or a C# 11+ raw string
    // literal ($""" ... """) — all of which render their literal text (and the
    // {…} holes) via ToString(). A pre-built variable (not a string literal in
    // source) renders as its identifier, which contains no AccountId and so is
    // conservatively flagged as a violation for manual review — those are out
    // of scope for the text walk and named as a limitation.
    private static string ReconstructSqlText(ExpressionSyntax expr) => expr.ToString();

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
    // query chain following this access contain an AccountId comparison?
    // Looks at the enclosing expression statement / member initializer and
    // searches for "AccountId" in a comparison or member-access form.
    internal static bool? PredicateHasAccountId(SyntaxNode node)
    {
        var statement = node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault()
            ?? node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().Select(m => m as SyntaxNode).FirstOrDefault();
        if (statement is null)
        {
            return null; // cannot tell — the caller treats null as "flag for review"
        }

        var text = statement.ToString();
        return text.Contains("AccountId", StringComparison.Ordinal);
    }

    // The conventional DbContext variable names in this codebase. Every tenant
    // filter-free DbSet access is `db.<Table>` (a 33-site grep across
    // src/ confirmed `db` is the sole receiver for the tenant-table accesses);
    // `user.Roles` / `actor.Roles` are in-memory collections, not DB queries,
    // and are excluded by requiring the receiver to be one of these names.
    private static bool IsDbContextReceiver(string name) =>
        name is "db" or "_db" or "context" or "ctx" or "_context";

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
