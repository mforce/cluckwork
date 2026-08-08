namespace Cluckwork.Application.Tests.Common;

using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Cluckwork.Application.Common;

// #258 root-cause fix (symptom fixed by #247): the SPA's audit-log action
// filter and entity-type labels are driven by two hand-maintained lists in
// web/src/i18n/enums.ts (AUDIT_ACTION_VALUES, ENTITY_TYPE_VALUES). Nothing
// previously kept them in sync with what the server actually emits — a new
// audit.WriteAsync(...) call site silently fell out of the admin filter
// and/or rendered its entityType as a raw code instead of a translated label.
//
// Two things close that gap:
//   1. AuditActions hoists every action string into ONE reflectable registry
//      (mirrors #231's validator-errorCode guard) — a raw string literal can no
//      longer reach WriteAsync's action argument without failing this test,
//      which also means the vocabulary is enumerable without re-deriving it by
//      grepping call sites (impossible in general: IdentityProvider's
//      ResetPasswordAndRevokeAsync takes its action as a PARAMETER, so the
//      literal lives at its two callers, not at the WriteAsync call itself).
//   2. entityType has no such indirection today (every call site is either
//      nameof(Type) or a bare literal), so it is walked directly from every
//      audit.WriteAsync(...) call site under src/ — found by scanning every
//      .cs file rather than a hand-picked project list, so a new project
//      cannot silently create an unwatched call site.
public sealed class AuditVocabularyCoverageTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cluckwork.sln")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the Cluckwork repository root.");
    }

    private sealed record AuditCallSite(string File, int Line, string EntityType);

    [Fact]
    public void Every_call_site_passes_its_action_through_the_AuditActions_registry()
    {
        // FindCallSites() asserts this per call site (see AssertActionIsRegistryReference);
        // this fact exists so a violation gets its own clearly-named failure rather
        // than surfacing under the entity-type fact that happens to call it too.
        var callSites = FindCallSites();
        Assert.NotEmpty(callSites); // guard the guard: the scanner actually found something
    }

    [Fact]
    public void AuditActions_registry_matches_the_SPA_AUDIT_ACTION_VALUES_list()
    {
        var serverActions = typeof(AuditActions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();
        Assert.NotEmpty(serverActions);

        var clientActions = ParseTsStringArray("AUDIT_ACTION_VALUES");

        AssertSetsMatch(
            "AuditActions vs web/src/i18n/enums.ts AUDIT_ACTION_VALUES", serverActions, clientActions);
    }

    [Fact]
    public void Observed_entity_types_match_the_SPA_ENTITY_TYPE_VALUES_list()
    {
        var serverEntityTypes = FindCallSites().Select(c => c.EntityType).ToHashSet();
        Assert.NotEmpty(serverEntityTypes);

        var clientEntityTypes = ParseTsStringArray("ENTITY_TYPE_VALUES");

        AssertSetsMatch(
            "observed audit.WriteAsync entityType arguments vs web/src/i18n/enums.ts ENTITY_TYPE_VALUES",
            serverEntityTypes, clientEntityTypes);
    }

    // Every audit.WriteAsync(...) call site under src/, found by walking every
    // .cs file rather than trusting a maintained list of "the projects that
    // audit things" — the #258 fix is precisely about not trusting a
    // maintained list to stay complete.
    private static List<AuditCallSite> FindCallSites()
    {
        var callSites = new List<AuditCallSite>();
        var srcRoot = Path.Combine(RepositoryRoot, "src");
        var separator = Path.DirectorySeparatorChar;

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{separator}obj{separator}") || file.Contains($"{separator}bin{separator}"))
                continue;

            // Relative to src/, with '/' normalized regardless of OS — so error
            // messages, and the known-exception key below, never bake in this
            // machine's absolute path.
            var relativePath = Path.GetRelativePath(srcRoot, file).Replace(separator, '/');

            var source = File.ReadAllText(file);
            // Matched against a COMMENT-STRIPPED view (blanked to same-length
            // whitespace, so indices still line up with `source`) — this file's
            // own doc comments below mention "audit.WriteAsync(" in prose, and a
            // guard that only works as long as nobody ever writes that phrase in
            // a comment again is not a guard worth trusting.
            var searchable = StripComments(source);

            // Every distinct identifier (parameter or field) declared with type
            // IAuditWriter in this file — whatever name a handler injects it
            // under (`audit`, `_audit`, `auditWriter`, ...). Matching the fixed
            // literal name "audit" would silently miss a differently-named
            // injection, which is exactly how a new call site could bypass every
            // check below (codex review of #439).
            var auditWriterNames = Regex.Matches(searchable, @"\bIAuditWriter\s+(\w+)")
                .Select(m => m.Groups[1].Value)
                .Distinct();

            foreach (var receiver in auditWriterNames)
            {
                foreach (Match call in Regex.Matches(searchable, $@"\b{Regex.Escape(receiver)}\.WriteAsync\s*\("))
                {
                    var (args, _) = SplitTopLevelArguments(source, call.Index + call.Length);
                    var line = source[..call.Index].Count(c => c == '\n') + 1;

                    AssertActionIsRegistryReference(relativePath, line, args[0]);
                    callSites.Add(
                        new AuditCallSite(relativePath, line, ResolveEntityType(relativePath, line, args[1])));
                }
            }

            // IdentityProvider.ResetPasswordAndRevokeAsync forwards its action
            // through a parameter rather than passing it to WriteAsync directly
            // (see AssertActionIsRegistryReference's exemption below) — so the
            // registry check has to happen at ITS call sites instead. Every
            // caller today AND any future one is checked here, not just the two
            // that exist when this guard was written (codex review of #439).
            foreach (Match call in Regex.Matches(searchable, @"\bResetPasswordAndRevokeAsync\s*\("))
            {
                var (args, endIndex) = SplitTopLevelArguments(source, call.Index + call.Length);
                var line = source[..call.Index].Count(c => c == '\n') + 1;

                // The regex also matches the method's own DECLARATION, which has
                // the identical "name(" shape. Distinguish by what follows the
                // parameter list: a declaration continues into a block body
                // ('{'), a call ends its statement ('...);'). Skip whitespace
                // between ')' and that token.
                var afterArgs = endIndex;
                while (afterArgs < searchable.Length && char.IsWhiteSpace(searchable[afterArgs])) afterArgs++;
                if (afterArgs < searchable.Length && searchable[afterArgs] == '{')
                    continue;

                // (user, newPassword, auditAction, reason, details[, ct]) — every
                // call site today uses positional arguments, so index 2 is
                // auditAction. A caller that switched to named arguments would
                // shift what sits at index 2 to something that does not match
                // AuditActions.X (or the ternary shape) below, which fails this
                // test loudly rather than silently validating the wrong slot.
                Assert.True(args.Length >= 3,
                    $"{relativePath}:{line} — ResetPasswordAndRevokeAsync call has fewer than 3 arguments; " +
                    "expected (user, newPassword, auditAction, ...).");
                AssertActionIsRegistryReference(relativePath, line, args[2]);
            }
        }

        return callSites;
    }

    // Replaces `//` and `/* */` comment content with same-length whitespace, so
    // an offset found in the returned string is still valid against the
    // ORIGINAL source (used by SplitTopLevelArguments) — nothing is deleted,
    // only blanked, and newlines are preserved so line-number arithmetic stays
    // correct. String literals are tracked separately so a `//` or `/*` INSIDE
    // one is never mistaken for a comment start.
    private static string StripComments(string source)
    {
        var result = new StringBuilder(source.Length);
        var inString = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLineComment)
            {
                result.Append(c == '\n' ? '\n' : ' ');
                if (c == '\n') inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    result.Append(' ').Append(' ');
                    i++;
                    inBlockComment = false;
                }
                else
                {
                    result.Append(c == '\n' ? '\n' : ' ');
                }
                continue;
            }

            if (inString)
            {
                result.Append(c);
                if (c == '\\' && next != '\0') { result.Append(next); i++; continue; }
                if (c == '"') inString = false;
                continue;
            }

            if (c == '"') { inString = true; result.Append(c); continue; }
            if (c == '/' && next == '/') { inLineComment = true; result.Append(' ').Append(' '); i++; continue; }
            if (c == '/' && next == '*') { inBlockComment = true; result.Append(' ').Append(' '); i++; continue; }

            result.Append(c);
        }

        return result.ToString();
    }

    // Splits the arguments of a call whose '(' ends at `openParenEnd`, respecting
    // nested (), {}, [] and string literals so a comma inside e.g. `new { a, b }`
    // or a namespace-qualified nameof(...) is never mistaken for an argument
    // separator. One forward pass, depth tracked by a counter — no backtracking
    // to reason about. Also returns the index right after the matching ')', so
    // a caller can peek at what follows (a declaration continues into '{'; a
    // call ends the statement with ';' — see FindCallSites' declaration guard).
    private static (string[] Args, int EndIndex) SplitTopLevelArguments(string source, int openParenEnd)
    {
        var args = new List<string>();
        var depth = 0;
        var argStart = openParenEnd;
        var inString = false;

        for (var i = openParenEnd; i < source.Length; i++)
        {
            var c = source[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; } // skip the escaped character
                if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '(' or '{' or '[':
                    depth++;
                    break;
                case ')' when depth == 0:
                    args.Add(source[argStart..i]);
                    return ([.. args], i + 1);
                case ')' or '}' or ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    args.Add(source[argStart..i]);
                    argStart = i + 1;
                    break;
            }
        }

        throw new InvalidOperationException(
            $"Unterminated audit.WriteAsync( call starting near offset {openParenEnd} — unbalanced parens.");
    }

    // The one place today where WriteAsync's action argument is a bare
    // parameter, not a direct AuditActions reference: IdentityProvider's
    // ResetPasswordAndRevokeAsync (#165/#265's shared core) takes `auditAction`
    // as a parameter because it has two distinct callers — SetUserPasswordAsync
    // and BreakGlassResetAsync — that each pass a DIFFERENT action. Both of
    // THOSE call sites pass an AuditActions member (AuditActions.UserPasswordSet
    // / AuditActions.UserBreakGlassReset respectively), so the value reaching
    // WriteAsync here is always a registry member, one call frame removed from
    // this line — verified by reading, not assumed. Keyed by file+LINE (not
    // just file) so moving this call even one line, or a new dynamic call site
    // appearing anywhere else, fails this test and forces a human to
    // re-confirm the exemption still applies rather than silently widening.
    private static readonly HashSet<(string File, int Line)> KnownIndirectActionCallSites =
    [
        ("Cluckwork.Infrastructure/Identity/IdentityProvider.cs", 549),
    ];

    // Fails closed on anything but a direct AuditActions.X reference, a
    // `cond ? AuditActions.A : AuditActions.B` ternary of two such references,
    // or the one documented indirect call site above — an inline string
    // literal (the exact regression #258 exists to prevent) trips this
    // immediately instead of silently bypassing AuditActions' registry (and
    // therefore the coverage check above).
    private static void AssertActionIsRegistryReference(string file, int line, string actionArg)
    {
        if (KnownIndirectActionCallSites.Contains((file, line))) return;

        var normalized = Regex.Replace(actionArg, @"\s+", " ").Trim();
        var isDirectReference = Regex.IsMatch(normalized, @"^AuditActions\.\w+$");
        var isTernaryOfReferences = Regex.IsMatch(
            normalized, @"^.+\?\s*AuditActions\.\w+\s*:\s*AuditActions\.\w+$");

        Assert.True(isDirectReference || isTernaryOfReferences,
            $"{file}:{line} — audit.WriteAsync's action argument is '{actionArg}', not a reference " +
            "to AuditActions. Add a constant to AuditActions and reference it here instead of an " +
            "inline literal, so this action is covered by the AUDIT_ACTION_VALUES coverage check (#258).");
    }

    // entityType has no indirection today: every call site names it directly,
    // either nameof(Type) (any namespace qualification — nameof itself already
    // strips that at compile time, so this mirrors that by taking the last
    // dotted segment) or a bare quoted literal. Anything else is an
    // unrecognized shape and fails loud rather than silently under-counting.
    private static string ResolveEntityType(string file, int line, string entityTypeArg)
    {
        var trimmed = entityTypeArg.Trim();

        var nameofMatch = Regex.Match(trimmed, @"^nameof\(\s*(?:[\w]+\.)*(\w+)\s*\)$");
        if (nameofMatch.Success) return nameofMatch.Groups[1].Value;

        var literalMatch = Regex.Match(trimmed, "^\"(\\w+)\"$");
        if (literalMatch.Success) return literalMatch.Groups[1].Value;

        throw new InvalidOperationException(
            $"{file}:{line} — audit.WriteAsync's entityType argument '{entityTypeArg}' is neither " +
            "nameof(Type) nor a bare quoted literal; teach ResolveEntityType this shape.");
    }

    // web/src/i18n/enums.ts's `export const NAME = [...] as const;` array —
    // parsed as text (this is a C# test with no TypeScript runtime available),
    // not derived from the compiled type NAME's own literal union, which is
    // exactly the vocabulary this test exists to check.
    private static HashSet<string> ParseTsStringArray(string constantName)
    {
        var enumsPath = Path.Combine(RepositoryRoot, "web", "src", "i18n", "enums.ts");
        // Comment-stripped for the same reason as the C# side: removing a value
        // by commenting out its line (`// "Foo.Bar",`) is a natural edit, and
        // StripComments's "//"/"/* */" handling is language-agnostic — it only
        // needs to recognize double-quoted strings, which is exactly how every
        // value in this array is written (codex review of #439).
        var source = StripComments(File.ReadAllText(enumsPath));

        var declaration = Regex.Match(
            source, $@"export const {constantName} = \[(.*?)\]\s*as const;", RegexOptions.Singleline);
        Assert.True(declaration.Success,
            $"Could not find 'export const {constantName} = [...] as const;' in {enumsPath}.");

        var values = Regex.Matches(declaration.Groups[1].Value, "\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
        Assert.NotEmpty(values);
        return values;
    }

    private static void AssertSetsMatch(string label, HashSet<string> server, HashSet<string> client)
    {
        var missingFromClient = server.Except(client).OrderBy(x => x).ToList();
        var missingFromServer = client.Except(server).OrderBy(x => x).ToList();

        Assert.True(missingFromClient.Count == 0 && missingFromServer.Count == 0,
            $"{label} drifted."
            + (missingFromClient.Count > 0
                ? $"\nServer emits but the client is missing: {string.Join(", ", missingFromClient)}"
                : "")
            + (missingFromServer.Count > 0
                ? $"\nClient lists but no server call site emits: {string.Join(", ", missingFromServer)}"
                : ""));
    }
}
