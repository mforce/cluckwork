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
        // Every call an exemption waved through, keyed by that exemption. The
        // exemption names ONE call, and is verified below to have matched
        // exactly one — see KnownIndirectActionCallSites.
        var exempted = new Dictionary<(string File, string Parameter), List<(int Line, int Index, string Searchable)>>();
        // Non-declaration calls of the exempted forwarder, per file — so the
        // companion caller-check below can be proven non-vacuous.
        var forwarderCallCounts = new Dictionary<string, int>();
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

                    // The exemption is keyed on the ACTION ARGUMENT ITSELF —
                    // the forwarded parameter identifier — not on which method
                    // the call sits in. An earlier version derived the
                    // enclosing method with a declaration regex, and codex
                    // (#492, twice) showed that shape is a trap: C# member
                    // declarations the regex missed (generic methods,
                    // modifier-less members, explicit interface
                    // implementations) attributed their calls to the PREVIOUS
                    // declaration, so a call could inherit the exemption — and
                    // with the legitimate forwarding call removed, even an
                    // exactly-one count stayed green. Matching the argument
                    // text needs no notion of member declarations at all.
                    //
                    // The CALL INDEX rides along so the post-walk check can
                    // require the exempted call to sit inside the forwarder's
                    // own brace-matched body — without it, deleting the real
                    // forwarding call and introducing a variable of the same
                    // name in some other method would keep the count at one.
                    var normalizedAction = Regex.Replace(args[0], @"\s+", " ").Trim();
                    if (KnownIndirectActionCallSites.Contains((relativePath, normalizedAction)))
                    {
                        if (!exempted.TryGetValue((relativePath, normalizedAction), out var hits))
                            exempted[(relativePath, normalizedAction)] = hits = [];
                        hits.Add((line, call.Index, searchable));
                    }
                    else
                    {
                        AssertActionIsRegistryReference(relativePath, line, args[0]);
                    }
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

                forwarderCallCounts[relativePath] =
                    forwarderCallCounts.GetValueOrDefault(relativePath) + 1;

                // (user, newPassword, auditAction, reason, details[, ct]) — every
                // call site today uses positional arguments, so index 2 is
                // auditAction. A caller that switched to named arguments would
                // shift what sits at index 2 to something that does not match
                // AuditActions.X (or the ternary shape) below, which fails this
                // test loudly rather than silently validating the wrong slot.
                Assert.True(args.Length >= 3,
                    $"{relativePath}:{line} — ResetPasswordAndRevokeAsync call has fewer than 3 arguments; " +
                    "expected (user, newPassword, auditAction, ...).");
                // Never exempt: these are the CALLERS of the one exempted
                // forwarder, and they are precisely what has to keep passing a
                // registry member.
                AssertActionIsRegistryReference(relativePath, line, args[2]);
            }
        }

        // The exemption names ONE call, so hold it to exactly one — and hold the
        // rest of its story together too. Three assertions per exemption, each
        // there because losing it was shown (codex, #492, two rounds) to open a
        // path a mutation had "proven" closed:
        //
        //   1. EXACTLY ONE WriteAsync forwards the documented identifier. More
        //      than one: a new dynamic call reusing the name is being accepted
        //      unreviewed. Zero: the exemption is stale and reads as a live,
        //      reviewed decision while guarding nothing.
        //   2. The file still DECLARES the forwarder with that identifier as a
        //      string parameter. Without this, moving the forwarding call into
        //      some other method whose parameter happens to share the name
        //      would keep the count at one while detaching the exemption from
        //      the caller-check that justifies it.
        //   3. The caller-check actually ran — at least one non-declaration
        //      call of the forwarder was found and its action argument
        //      registry-checked. A renamed forwarder would otherwise make that
        //      companion loop silently match nothing, and the whole exemption
        //      would rest on a check that no longer executes.
        foreach (var (file, parameter) in KnownIndirectActionCallSites)
        {
            var matched = exempted.TryGetValue((file, parameter), out var hits) ? hits : [];
            Assert.True(matched.Count == 1,
                $"The indirect-action exemption for {file} (forwarded identifier '{parameter}') must cover "
                + $"EXACTLY ONE audit.WriteAsync call, but matched {matched.Count} "
                + $"(lines: {(matched.Count == 0 ? "none" : string.Join(", ", matched.Select(h => h.Line)))}). "
                + "Zero means the exemption is stale; more than one means a new dynamic call site is "
                + "being accepted unreviewed — give it a registry constant instead.");

            // The one exempted call must sit INSIDE the forwarder's own body.
            // Without this, deleting the real forwarding call and introducing a
            // variable of the same name in some other method keeps the count at
            // one while detaching the exemption from the caller-side check that
            // justifies it (codex, #492 round 2). BodySpanOf brace-matches ONE
            // known method's body — a depth counter over comment-stripped text,
            // the same machinery SplitTopLevelArguments already trusts — rather
            // than attempting general member parsing, which is the mistake the
            // two retired key shapes shared.
            var (hitLine, hitIndex, searchableSource) = matched[0];
            var body = BodySpanOf(searchableSource, "ResetPasswordAndRevokeAsync", parameter);
            Assert.True(body is not null,
                $"{file} no longer declares ResetPasswordAndRevokeAsync with a 'string {parameter}' "
                + "parameter. The exemption is justified by that forwarder's callers being checked; "
                + "if the forwarder was renamed or reshaped, rewrite the exemption to match.");
            Assert.True(hitIndex > body!.Value.Start && hitIndex < body.Value.End,
                $"{file}:{hitLine} — the exempted WriteAsync forwarding '{parameter}' is OUTSIDE "
                + "ResetPasswordAndRevokeAsync's body. The exemption covers that method's single "
                + "forwarding call and nothing else; a call elsewhere must reference AuditActions.");

            Assert.True(forwarderCallCounts.GetValueOrDefault(file) >= 1,
                $"No non-declaration call of ResetPasswordAndRevokeAsync was found in {file} — the "
                + "caller-side registry check ran zero times, so the exemption is resting on a check "
                + "that never executed.");
        }

        return callSites;
    }

    // The half-open span of `methodName`'s brace-delimited body in
    // comment-stripped source, or null when no declaration carrying
    // `string <parameter>` in its parameter list exists. Finds the declaration
    // by shape (name, then a parameter list containing the forwarded parameter,
    // then '{'), then walks braces with the same string-aware depth counting
    // SplitTopLevelArguments uses. Deliberately handles exactly one method —
    // this is not, and must not grow into, a general C# member parser.
    private static (int Start, int End)? BodySpanOf(string searchable, string methodName, string parameter)
    {
        foreach (Match declaration in Regex.Matches(searchable, $@"\b{Regex.Escape(methodName)}\s*\("))
        {
            var (parameters, endIndex) = SplitTopLevelArguments(searchable, declaration.Index + declaration.Length);
            if (!parameters.Any(p => Regex.IsMatch(p, $@"\bstring\s+{Regex.Escape(parameter)}\b")))
                continue;

            var i = endIndex;
            while (i < searchable.Length && char.IsWhiteSpace(searchable[i])) i++;
            if (i >= searchable.Length || searchable[i] != '{')
                continue; // a call, or an expression-bodied member — not the declaration

            var depth = 0;
            var inString = false;
            for (var j = i; j < searchable.Length; j++)
            {
                var c = searchable[j];
                if (inString)
                {
                    if (c == '\\') { j++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                    return (i, j);
            }
        }

        return null;
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
    // this line — verified by reading, not assumed.
    //
    // Keyed by file + the FORWARDED IDENTIFIER — the exact text of the action
    // argument at the one exempted call. The key has been through three shapes,
    // each retired by a demonstrated failure:
    //
    //   file + line: broke twice in one branch from unrelated insertions above
    //   it, and its failure message invited renumbering on faith.
    //
    //   file + enclosing method, derived by a declaration REGEX: codex (#492)
    //   showed the regex misattributes calls in members it cannot parse
    //   (generic methods, modifier-less members, explicit interface
    //   implementations) to the PREVIOUS declaration — so a dynamic call could
    //   inherit the exemption, and with the legitimate forwarding call removed,
    //   even an exactly-one count stayed green. Parsing C# member boundaries
    //   with a regex was the mistake; two rounds of patching it did not fix
    //   that, per the two-misses rule.
    //
    //   file + forwarded identifier (this shape): needs no notion of members at
    //   all. A call is exempt only if its action argument is EXACTLY this
    //   identifier, in this file. FindCallSites then holds the story together:
    //   exactly one such call, the forwarder still declares the identifier as a
    //   string parameter, and the forwarder's callers were actually checked.
    private static readonly HashSet<(string File, string Parameter)> KnownIndirectActionCallSites =
    [
        ("Cluckwork.Infrastructure/Identity/IdentityProvider.cs", "auditAction"),
    ];

    // Fails closed on anything but a direct AuditActions.X reference, a
    // `cond ? AuditActions.A : AuditActions.B` ternary of two such references,
    // or the one documented indirect call site above — an inline string
    // literal (the exact regression #258 exists to prevent) trips this
    // immediately instead of silently bypassing AuditActions' registry (and
    // therefore the coverage check above).
    private static void AssertActionIsRegistryReference(string file, int line, string actionArg)
    {
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
