namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers;
using Cluckwork.Infrastructure.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

// #417 — guards for the generated schema documentation under docs/schema/
// (produced by tools/schema-docs/generate.sh via tbls against an ephemeral
// migrated Postgres). The CI byte-diff (`generate.sh --check`) proves the
// committed docs are exactly what the generator produces on another machine;
// these tests prove the CONTENT is right and fail BY NAME (#407: a mystery
// digest mismatch invites re-baselining, a named failure invites a fix).
//
// All three are walk-everything guards, deliberately not hand-kept lists
// (#407: two misses of the same shape mean the method is wrong):
//   1. every postgres image pin in the repo is one identical digest-pinned
//      string — the docs must be generated against the same Postgres the
//      tests validate and the stacks run;
//   2. the committed docs carry no environment leakage;
//   3. every table, index, and check constraint that actually exists in a
//      freshly migrated database appears in the committed docs — a new
//      expression index added by a future migration is in scope automatically.
public sealed class SchemaDocsTests
{
    private const string PostgresImage =
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";

    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string DocsDir = Path.Combine(RepoRoot, "docs", "schema");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cluckwork.sln")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Cluckwork.sln not found above the test bin directory.");
    }

    private static AppDbContext BuildContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        new PostgresDbContextConfigurator().Configure(options, connectionString, new DatabaseResilienceOptions());
        options.AddInterceptors(new TenantStampInterceptor(new TenantContext()));
        return new AppDbContext(options.Options, new TenantContext());
    }

    // Marks lines that are the BODY of a YAML block scalar (|/> values, e.g.
    // run: steps' shell/jq text): a body line is indented deeper than the
    // scalar's key and is embedded text, not YAML structure. Blank lines
    // inside the body stay masked; the first non-blank line at or above the
    // key's indent ends the scalar.
    private static bool[] BlockScalarBodyMask(string[] lines)
    {
        var mask = new bool[lines.Length];
        var scalarKeyIndent = -1;
        // The scalar boundary is the KEY's column, not the line's leading
        // whitespace: for a sequence item (`- run: |`), sibling keys of the
        // same item sit past the dash, so measuring from the dash would
        // swallow them into the body. The prefix group spans indentation
        // plus any dash markers.
        var opener = new Regex(@"^(?<prefix>[ \t]*(?:-[ \t]+)*)[^#\s][^\r\n]*:[ \t]*[|>][+-]?[0-9]*[ \t]*(?:#[^\r\n]*)?\r?$");
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart(' ', '\t');
            var indent = line.Length - trimmed.Length;
            if (scalarKeyIndent >= 0)
            {
                if (trimmed.TrimEnd('\r').Length == 0) { mask[i] = true; continue; }
                if (indent > scalarKeyIndent) { mask[i] = true; continue; }
                scalarKeyIndent = -1;
            }
            var m = opener.Match(line);
            if (m.Success) scalarKeyIndent = m.Groups["prefix"].Length;
        }
        return mask;
    }

    private static IReadOnlyList<string> TrackedFiles()
    {
        // git ls-files, not a directory walk: untracked local clutter must not
        // produce false reds, and a tracked pin must never escape the sweep.
        var psi = new ProcessStartInfo("git", "ls-files -z")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
        };
        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        Assert.Equal(0, proc.ExitCode);
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    // Every copy of the postgres image pin — test factories, CI, compose
    // stacks, and the schema-docs generator — must be the SAME digest-pinned
    // string. Before #417 this was enforced by code comments only; a drifted
    // copy means docs generated (or tests run) against a different Postgres
    // than production-like stacks use.
    [Fact]
    public void PostgresImagePin_IsOneIdenticalStringAcrossEveryTrackedFile()
    {
        // Discover, THEN validate: a pattern that only matched digest-pinned
        // strings would let a new latest-tagged (or otherwise unpinned)
        // postgres reference escape the sweep entirely. This candidate
        // pattern matches any postgres image reference — pinned or not — and
        // every match must equal the canonical pin. `(?!//)` keeps
        // `postgres://` connection URIs (generate.sh's DSN) out of scope.
        // (Written to avoid containing a matching literal itself — this file
        // is inside its own sweep.)
        var candidatePattern = new Regex(@"postgres:(?!//)[A-Za-z0-9][A-Za-z0-9._-]*(?:@sha256:[0-9a-f]{64})?");
        // A reference with NO tag at all (compose `image: postgres`,
        // Dockerfile `FROM postgres`) is valid and floats to latest — it has
        // no colon for the pattern above to see, so it needs its own
        // detector, scoped to the two syntaxes where a bare name is a live
        // image reference rather than prose.
        // Namespaced/registry-qualified forms (docker.io/library/postgres,
        // registry:5000/ns/postgres) float to latest exactly the same way,
        // so the optional prefix segments are part of the detector.
        // YAML allows the image key itself to be quoted or to carry space
        // before the colon — every image-key pattern shares this spelling.
        const string ImageKey = @"[""']?image[""']?[ \t]*:";
        // COPY --from= consumes an external image when the name isn't a
        // build stage — a third syntax where a bare name is a live reference.
        var untaggedPattern = new Regex(@"(?im)^\s*(?:" + ImageKey + @"\s*|FROM\s+)[""']?(?:[a-z0-9.-]+(?::\d+)?/)*postgres[""']?(?=\s|$)");
        // COPY --from= UNANCHORED (like the mount rules): a continued COPY
        // puts --from= on a line that no longer starts with COPY, and the
        // token is unambiguous wherever it appears.
        var copyFromPattern = new Regex(@"(?im)--from=[""']?(?:[a-z0-9.-]+(?::\d+)?/)*postgres[""']?(?=\s|$)");
        // Compose additional_contexts may back a build context with an image
        // via the docker-image:// scheme — a bare name there floats to
        // latest like any untagged reference.
        var dockerImageContextPattern = new Regex(
            @"(?im)docker-image://[""']?(?:[a-z0-9.-]+(?::\d+)?/)*postgres[""']?(?=[,\s""'}\]]|$)");
        // An interpolated docker-image:// reference is never a reviewable
        // pin, whatever the variable's name — same name-blind refusal as a
        // variable FROM image (a scheme-prefixed value is always an image).
        var dockerImageVarPattern = new Regex(@"(?im)docker-image://[^\s""',}\]]*\$");
        // A postgres-reference-shaped C# string literal (a Testcontainers
        // image constant, e.g.) must BE the canonical pin — a bare
        // "postgres" literal floats to latest with no colon for the global
        // candidate to see.
        // Scoped to IMAGE-CONSUMING expressions — an *Image* assignment, a
        // *Builder("...") construction, or WithImage("...") — because the
        // bare word is ALSO a legitimate scheme name, username, and database
        // name in C# sources (PostgresConnectionString's scheme handling and
        // the design-time factory's DSN fixtures surfaced as baseline false
        // positives when the sweep ran anchor-free). An image reference fed
        // through any other expression is opaque indirection, same boundary
        // as variable names.
        // Ordinary, verbatim (@\"...\"), raw, and interpolated literal
        // syntaxes all count. Raw strings permit ANY delimiter of three or
        // more quotes, and interpolation stacks any number of $ signs
        // ($$\"\"\"...\"\"\"), so the prefix is an unbounded [$@] run and
        // the opening quote run is captured unbounded and matched at the
        // close — never a hand-capped length. A multiline raw literal strips
        // the closing delimiter's indentation, so a value written across
        // lines still evaluates to the bare reference — whitespace (incl.
        // newlines) around the reference is skipped on both sides, and the
        // tag class excludes whitespace so trailing indentation never folds
        // into the captured value.
        var csharpImageLiteralPattern = new Regex(
            @"(?:Image\w*\s*=\s*|Builder\s*\(\s*(?:\w+\s*:\s*)?|WithImage\s*\(\s*(?:\w+\s*:\s*)?)[$@]*(?<q>""+)\s*(?<img>(?:[a-z0-9.-]+(?::\d+)?/)*postgres(?::[^""@\s]+)?(?:@sha256:[0-9a-f]{64})?)\s*\k<q>");
        // BuildKit RUN mounts pull an external image when from= names no
        // build stage or context — a fourth bare-reference syntax.
        // NOT anchored to RUN: a continued instruction puts later mount
        // options on lines that no longer start with RUN, and a --mount=
        // token is unambiguous wherever it appears.
        // from may be the FIRST option (type defaults to bind), so the
        // preceding-option prefix is optional.
        var runMountFromPattern = new Regex(
            @"(?im)--mount=(?:[^\s]*[,=])?from=[""']?(?:[a-z0-9.-]+(?::\d+)?/)*postgres[""']?(?=[,\s""']|$)");
        // A mount option token cut by a line continuation defers its option
        // text (including a possible from=) past the line — refused as a
        // shape. A COMPLETE option followed by space-then-continuation is
        // ordinary multi-line RUN formatting and stays legitimate: the
        // refusal fires only when the continuation char ABUTS the token.
        // Terminal form (rounds 60/63/64 each found a narrower splice): in a
        // Dockerfile, a continuation character ABUTTING any token splices
        // the next physical line into that token — option names as much as
        // option values — so token-abutting continuations are refused
        // wholesale. Ordinary formatting puts a space before the
        // continuation and stays legitimate. Scoped to Dockerfile-named
        // files: backslash-abutting line ends are legit syntax in shell and
        // C# sources.
        var runMountContinuedPattern = new Regex(
            @"(?im)[^\s\\`][\\`][ \t]*\r?$");
        // Digest-only pull grammar (NAME@DIGEST, no tag): immutable but not
        // the canonical reference — and tagless, so neither pattern above
        // sees it (no colon for the first, an @ failing the second's
        // end-of-token lookahead).
        var digestOnlyPattern = new Regex(@"postgres@sha256:[0-9a-f]{64}");
        // A compose-interpolated tag (a dollar-brace variable where the tag
        // belongs) resolves at runtime to whatever the operator's env says —
        // by definition not a reviewable pin, and invisible to all three
        // detectors above (the candidate pattern requires an alphanumeric
        // after the colon). Comment deliberately avoids spelling the literal
        // — this file is inside its own sweep.
        var interpolatedPattern = new Regex(@"postgres:\$\{?[A-Za-z_][A-Za-z0-9_:-]*\}?");
        // ALLOW-LIST, not marker enumeration: rounds 10-16 of review each
        // produced one more YAML syntax that defers or indirects an image
        // value (block scalars, comments, anchors, explicit tags...). The
        // rule is now inverted — an image key's value must BE a plain
        // same-line literal in image-reference shape; any other value is
        // refused sight unseen, whatever mechanism it uses. A bare/comment-
        // only value needs one lookahead, because `image:` is also how a
        // MAPPING named image opens (ci.yml's job id): a mapping's next
        // line is another key, a deferred value's next line is a scalar.
        // FROM lines get the same treatment: a backslash continuation or a
        // postgres-naming interpolated value is refused. The remaining
        // boundary is unchanged: a variable whose name does not identify
        // postgres is opaque indirection no text scan can close.
        // Node properties (a tag or anchor) and sequence dashes may precede
        // the key itself — a tagged plain key still resolves to `image`.
        var imageKeyLinePattern = new Regex(@"^[ \t]*(?:-[ \t]+)*(?:[&!][^\s]+[ \t]+)*" + ImageKey + @"[ \t]*(?<value>[^\r\n]*?)[ \t]*\r?$",
            RegexOptions.IgnoreCase);
        var literalImageValuePattern = new Regex(@"^[""']?[A-Za-z0-9][A-Za-z0-9._:@/-]*[""']?(?:[ \t]+#[^\r\n]*)?$");
        // Deliberate exception: a GitHub Actions expression carrying a PRIOR
        // JOB'S output (steps./needs.) is workflow-internal dataflow — the
        // #351 promotion flow requires exactly this shape (the digest comes
        // from CI's own run artifact, never a declared pin). Operator-
        // settable expression roots (env., vars., inputs.) are NOT excepted.
        // Exactly ONE direct output reference, no operators: an expression
        // like `steps.x.outputs.image || vars.Y` must not ride in on the
        // steps. prefix — a fallback is an operator-settable escape hatch.
        var workflowOutputPattern = new Regex(@"^\$\{\{\s*(?:steps|needs)\.[A-Za-z0-9_-]+\.outputs\.[A-Za-z0-9_-]+\s*\}\}$");
        // Flow-style mappings put the image key mid-line where the
        // line-anchored rule can't see it — refused wholesale in YAML files
        // (declare the image as a block mapping key). The brace must be a
        // YAML VALUE (preceded by `key:` or a sequence dash on the line):
        // that keeps shell-embedded brace text inside run: blocks — e.g.
        // release-please.yml's jq program building the #351 image.json —
        // out of scope, since it opens behind a quote, not a YAML key.
        // Scoped by extension: brace objects in JSON/JS are not compose.
        // The image key may sit past NESTED braces inside the flow mapping,
        // so everything on the line after the value-position brace is in
        // scope — not just up to the first closing brace.
        // YAML node properties (an &anchor or !tag) may sit between the
        // value-position colon and the opening brace — they are part of the
        // same flow-mapping shape.
        // Value-position flow mappings are refused ENTIRELY, except the
        // empty `{}` idiom (permissions: {}, batch: {}) — an empty mapping
        // can hide nothing. Rounds 17-21 each defeated a narrower flow rule
        // (nesting, anchors, continuations, quoted braces); total refusal
        // ends the analysis: no brace counting, no quote awareness, no
        // multi-line joining, nothing left to hide an image key in. The
        // value-position anchor keeps shell-embedded brace text — the
        // release-please.yml jq program — out of scope, as before.
        // Node properties may precede the OUTER key as well as the brace
        // (&anchor key: { ... }).
        // The outer key may be a QUOTED scalar containing whitespace — the
        // quoted alternatives consume it with escape awareness.
        var flowMappingPattern = new Regex(
            @"^[ \t]*(?:-[ \t]+)*(?:[&!][^\s]+[ \t]+)*(?:-[ \t]+|(?:[A-Za-z0-9_.""'-]+|""(?:[^""\\]|\\.)*""|'(?:[^']|'')*')[ \t]*:[ \t]*)(?:[&!][^\s{]+[ \t]+)*\{(?!\}[ \t]*(?:#[^\r\n]*)?\r?$)");
        // A document that is ITSELF a flow mapping — optionally indented,
        // optionally behind a --- document marker. Distinguishing this from
        // the shell/jq JSON embedded in run: block scalars is NOT a text-
        // shape question (both are indented brace lines), so every YAML line
        // rule runs behind a block-scalar mask: lines inside a |/> body are
        // embedded text and skipped; everything else is YAML structure.
        var rootFlowPattern = new Regex(
            @"^[ \t]*(?:---[ \t]+)?(?:[&!][^\s{]+[ \t]+)*\{(?!\}[ \t]*(?:#[^\r\n]*)?\r?$)");
        // A double-quoted mapping key carrying escape sequences can resolve
        // to ANY key ("image" is image) — refusing to decode is the
        // point: a key that needs escapes is not reviewable as text, so the
        // shape itself is rejected, whatever it decodes to.
        var escapedKeyPattern = new Regex(@"^[ \t]*(?:-[ \t]+)*(?:[&!][^\s""]+[ \t]+)*""[^""\r\n]*\\[^""\r\n]*""[ \t]*:");
        var fromLinePattern = new Regex(@"(?im)^[ \t]*FROM[ \t][^\r\n]*");
        // A bare (untagged) postgres reference hiding in an ARG default
        // (ARG X=postgres) floats to latest when the ARG parameterizes FROM.
        var argPostgresDefaultPattern = new Regex(
            @"(?im)^[ \t]*ARG[ \t]+[A-Za-z_][A-Za-z0-9_]*=[""']?(?:[a-z0-9.-]+(?::\d+)?/)*postgres[""']?(?=\s|$)");
        // A FROM whose image is entirely a variable is never reviewable —
        // whatever the variable's name, its value comes from ARG/build args.
        // (FROM context is narrow enough that this needs no name heuristic,
        // unlike compose values.)
        // FROM options (--platform=...) may precede the image token, and a
        // variable may expand ANYWHERE within it (registry/${VAR} as much as
        // ${VAR} alone) — any dollar in the image token refuses the line.
        var fromVariablePattern = new Regex(@"(?im)^[ \t]*FROM[ \t]+(?:--[A-Za-z0-9-]+=[^\s]+[ \t]+)*[^\s]*\$");
        var pgNamePattern = new Regex(@"postgres|_pg_?|pg_", RegexOptions.IgnoreCase);
        var mappingKeyPattern = new Regex(@"^[ \t]*[A-Za-z0-9_.-]+:([ \t]|\r?$)");
        var hits = new Dictionary<string, List<string>>();

        foreach (var relative in TrackedFiles())
        {
            var path = Path.Combine(RepoRoot, relative);
            if (!File.Exists(path)) continue;
            var info = new FileInfo(path);
            if (info.Length > 5_000_000) continue; // binary-ish blobs are not config
            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException) { continue; }
            foreach (Match m in candidatePattern.Matches(text))
            {
                if (!hits.TryGetValue(m.Value, out var files))
                    hits[m.Value] = files = [];
                files.Add(relative);
            }
            foreach (Match m in untaggedPattern.Matches(text))
            {
                if (!hits.TryGetValue("postgres (untagged — floats to latest)", out var files))
                    hits["postgres (untagged — floats to latest)"] = files = [];
                files.Add(relative);
            }
            foreach (Match m in copyFromPattern.Matches(text))
            {
                if (!hits.TryGetValue("postgres (untagged, in a --from= — floats to latest)", out var files))
                    hits["postgres (untagged, in a --from= — floats to latest)"] = files = [];
                files.Add(relative);
            }
            foreach (Match m in dockerImageContextPattern.Matches(text))
            {
                if (!hits.TryGetValue("postgres (untagged, in a docker-image:// context — floats to latest)", out var files))
                    hits["postgres (untagged, in a docker-image:// context — floats to latest)"] = files = [];
                files.Add(relative);
            }
            foreach (Match m in dockerImageVarPattern.Matches(text))
            {
                if (!hits.TryGetValue("docker-image:// context via variable — not a reviewable pin", out var files))
                    hits["docker-image:// context via variable — not a reviewable pin"] = files = [];
                files.Add(relative);
            }
            foreach (Match m in runMountFromPattern.Matches(text))
            {
                if (!hits.TryGetValue("postgres (untagged, in a RUN mount from= — floats to latest)", out var files))
                    hits["postgres (untagged, in a RUN mount from= — floats to latest)"] = files = [];
                files.Add(relative);
            }
            if (relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match m in csharpImageLiteralPattern.Matches(text))
                {
                    var val = m.Groups["img"].Value;
                    if (val == PostgresImage) continue;
                    if (!hits.TryGetValue($"\"{val}\" (postgres-shaped C# string literal — not the canonical pin)", out var files))
                        hits[$"\"{val}\" (postgres-shaped C# string literal — not the canonical pin)"] = files = [];
                    files.Add(relative);
                }
            }
            if (relative.Contains("Dockerfile", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match m in runMountContinuedPattern.Matches(text))
                {
                    if (!hits.TryGetValue("token-abutting continuation — not reviewable", out var files))
                        hits["token-abutting continuation — not reviewable"] = files = [];
                    files.Add(relative);
                }
            }
            foreach (Match m in digestOnlyPattern.Matches(text))
            {
                if (!hits.TryGetValue(m.Value, out var files))
                    hits[m.Value] = files = [];
                files.Add(relative);
            }
            foreach (Match m in interpolatedPattern.Matches(text))
            {
                if (!hits.TryGetValue($"{m.Value} (interpolated — not a reviewable pin)", out var files))
                    hits[$"{m.Value} (interpolated — not a reviewable pin)"] = files = [];
                files.Add(relative);
            }
            void AddHit(string key)
            {
                if (!hits.TryGetValue(key, out var files)) hits[key] = files = [];
                files.Add(relative);
            }

            foreach (Match m in fromLinePattern.Matches(text))
            {
                var line = m.Value;
                // Either continuation character: backslash by default, or
                // backtick under the `escape` directive. No legitimate FROM
                // ends with either, so both are refused without parsing the
                // directive itself.
                if (line.TrimEnd().EndsWith('\\') || line.TrimEnd().EndsWith('`'))
                    AddHit($"{line.Trim()} (FROM continued past the line — not a reviewable pin)");
                else if (line.Contains('$') && pgNamePattern.IsMatch(line))
                    AddHit($"{line.Trim()} (interpolated image line — not a reviewable pin)");
            }
            foreach (Match m in argPostgresDefaultPattern.Matches(text))
                AddHit($"{m.Value.Trim()} (untagged postgres in an ARG default — floats to latest)");
            foreach (Match m in fromVariablePattern.Matches(text))
                AddHit($"{m.Value.Trim()}... (FROM via variable — not a reviewable pin)");

            var textLines = text.Split('\n');
            var isYaml = relative.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                || relative.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);
            var embedded = isYaml ? BlockScalarBodyMask(textLines) : new bool[textLines.Length];
            for (var i = 0; i < textLines.Length; i++)
            {
                if (embedded[i]) continue; // block-scalar body: embedded text, not YAML structure
                if (isYaml)
                {
                    // Explicit-key syntax (a line-leading `?` indicator)
                    // splits the key/value pairing across physical lines by
                    // design — refused as a class, like flow continuations,
                    // whatever key it spells.
                    if (textLines[i].TrimEnd().Length > 0
                        && Regex.IsMatch(textLines[i], @"^[ \t]*(?:-[ \t]+)*\?([ \t]|\r?$)"))
                    {
                        AddHit($"{textLines[i].Trim()} (explicit mapping key — use plain keys)");
                        continue;
                    }
                    // An alias in key position (*name: value) resolves to
                    // whatever scalar its anchor holds — key indirection,
                    // refused like escaped and explicit keys.
                    if (Regex.IsMatch(textLines[i], @"^[ \t]*(?:-[ \t]+)*\*[^\s:]+[ \t]*:([ \t]|\r?$)"))
                    {
                        AddHit($"{textLines[i].Trim()} (alias mapping key — not reviewable as text)");
                        continue;
                    }
                    if (escapedKeyPattern.IsMatch(textLines[i]))
                    {
                        AddHit($"{textLines[i].Trim()} (escaped mapping key — not reviewable as text)");
                        continue;
                    }
                    // A flow SEQUENCE in value position may carry inline
                    // mappings (a merge key's bracketed list, e.g.) or defer
                    // past its line; either hides keys from line rules.
                    // Refused when the sequence contains a MAPPING brace or
                    // never closes on its line. An interpolation brace
                    // (dollar-prefixed) is not a mapping opener — compose
                    // healthcheck arrays legitimately interpolate env vars
                    // inside their strings, which the first version of this
                    // rule flagged as a baseline false positive. The key
                    // charset includes < for merge keys.
                    // Compact mappings need no braces at all inside a flow
                    // sequence ([ image: value ]) — an ENTRY-POSITION key is
                    // an unquoted token-colon at the start of an entry, or a
                    // closing quote immediately followed by a colon; a colon
                    // INSIDE a quoted scalar is adjacent to neither, so
                    // string arrays stay clean.
                    // POSITION-FREE: the ': ' before the opener is the
                    // value-position marker, so the owner key's spelling
                    // (plain, quoted, whitespace-bearing — rounds 54-55 each
                    // defeated a key charset) no longer matters at all. The
                    // colon must not be part of an interpolation (': ${').
                    // Colon-value OR block-sequence-item position: a dash
                    // opens a sequence entry just as a mapping colon opens a
                    // value, and either may carry the flow opener.
                    var seq = Regex.Match(textLines[i],
                        @"(?::|^[ \t]*(?:-[ \t]+)*-)[ \t]*(?:[&!][^\s\[]+[ \t]+)*\[(?<rest>[^\r\n]*)");
                    var flowVal = Regex.Match(textLines[i],
                        @":[ \t]*(?:[&!][^\s{]+[ \t]+)*(?<!\$)\{(?!\}[ \t]*(?:#[^\r\n]*)?\r?$)");
                    if (flowVal.Success)
                    {
                        AddHit($"{textLines[i].Trim()} (flow-style mapping — use block mappings; only the empty {{}} idiom is allowed)");
                        continue;
                    }
                    // The CLOSE check must not be satisfied by a bracket
                    // living inside a quoted scalar or a trailing comment —
                    // evaluate it on a scope with quoted spans blanked and
                    // the comment tail cut.
                    // Escape-aware quote blanking: a double-quoted scalar may
                    // contain backslash-escaped quotes, a single-quoted one
                    // doubled quotes — a naive [^"]* reducer stops at the
                    // escape and leaves the scalar's tail (with any fake
                    // closer it carries) in scope.
                    // Closed pairs blank to NOTHING so that any quote left
                    // standing means an unterminated (multiline) scalar —
                    // whose tail could carry a fake closer on this line and
                    // real content on the next, so the opener is refused.
                    var closeScope = seq.Success
                        ? Regex.Replace(seq.Groups["rest"].Value, @"""(?:[^""\\]|\\.)*""|'(?:[^']|'')*'", "")
                        : "";
                    var hashIdx = closeScope.IndexOf('#');
                    if (hashIdx >= 0) closeScope = closeScope[..hashIdx];
                    if (seq.Success && (Regex.IsMatch(seq.Groups["rest"].Value, @"(?<!\$)\{")
                        || closeScope.Contains('"') || closeScope.Contains('\'')
                        || !closeScope.Contains(']')
                        || Regex.IsMatch(seq.Groups["rest"].Value, @"(?:^|,)[ \t]*(?:[&!][^\s\]]+[ \t]+)*[A-Za-z0-9_.-]+[ \t]*:[ \t]")
                        || Regex.IsMatch(seq.Groups["rest"].Value, @"[""'][ \t]*:")
                        // Explicit compact pairs (an entry-position ? key
                        // indicator, node props allowed before it).
                        || Regex.IsMatch(seq.Groups["rest"].Value, @"(?:^|,)[ \t]*(?:[&!][^\s\]]+[ \t]+)*\?([ \t]|\]|$)")
                        // Alias compact keys (an entry-position *name:).
                        || Regex.IsMatch(seq.Groups["rest"].Value, @"(?:^|,)[ \t]*\*[^\s:\]]+[ \t]*:([ \t]|\]|$)")))
                    {
                        AddHit($"{textLines[i].Trim()} (flow sequence carrying mappings or deferring past the line — not reviewable)");
                        continue;
                    }
                    if (flowMappingPattern.IsMatch(textLines[i]))
                    {
                        AddHit($"{textLines[i].Trim()} (flow-style mapping — use block mappings; only the empty {{}} idiom is allowed)");
                        continue;
                    }
                    if (rootFlowPattern.IsMatch(textLines[i]))
                    {
                        AddHit($"{textLines[i].Trim()} (root-level flow document — use a block mapping document)");
                        continue;
                    }
                }
                var m = imageKeyLinePattern.Match(textLines[i]);
                if (!m.Success) continue;
                var value = m.Groups["value"].Value.Trim();
                if (value.Length == 0 || value.StartsWith('#'))
                {
                    var next = textLines.Skip(i + 1).FirstOrDefault(l => l.Trim().Length > 0) ?? "";
                    if (mappingKeyPattern.IsMatch(next)) continue; // a mapping named "image", not an image key
                    AddHit("image: (value deferred to the next line — not a reviewable pin)");
                }
                else if (!literalImageValuePattern.IsMatch(value) && !workflowOutputPattern.IsMatch(value))
                {
                    AddHit($"{textLines[i].Trim()} (image value is not a plain same-line literal — not a reviewable pin)");
                }
            }
        }

        Assert.True(hits.Count > 0, "No postgres image reference found anywhere — the sweep itself is broken.");
        Assert.True(hits.Count == 1 && hits.ContainsKey(PostgresImage),
            "Postgres image references that are not the canonical digest-pinned string:\n" + string.Join("\n",
                hits.Where(kv => kv.Key != PostgresImage)
                    .Select(kv => $"  {kv.Key}\n    in: {string.Join(", ", kv.Value.Distinct())}")));
    }

    // The committed docs must be machine-independent: any absolute path,
    // timestamp, or credential-bearing DSN in them means the generator leaked
    // its environment, and the CI regen-diff would fail as an unexplainable
    // byte mismatch instead of this named assertion (#407: prove portability,
    // not repetition).
    [Fact]
    public void CommittedSchemaDocs_CarryNoEnvironmentSpecificContent()
    {
        Assert.True(Directory.Exists(DocsDir),
            "docs/schema/ does not exist — run tools/schema-docs/generate.sh");

        // The path rule is deliberately BROAD (any /-leading token, not a
        // list of known prefixes): the generator's own /work container mount
        // is identical on every machine, so a leak of it would survive the
        // CI byte-diff — this guard is the only thing that can catch that
        // class. Verified against the real tbls output: legitimate content
        // contains no /-leading tokens at all.
        var leaks = new (string Name, Regex Pattern)[]
        {
            // The preceder class includes ':' so URI-wrapped paths
            // (source:/work/...) are caught; https:// URLs stay clean
            // because their second slash breaks the segment charset.
            ("absolute unix path", new Regex(@"(?m)(?:^|[\s(""'`=:])/[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]*)*")),
            // file:// URIs always wrap a local path — a leak regardless of
            // how many slashes follow the scheme.
            ("file URI", new Regex(@"(?i)file://")),
            ("absolute windows path", new Regex(@"(?i)[a-z]:\\")),
            ("timestamp", new Regex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}")),
            ("connection URI (would carry the ephemeral password)", new Regex(@"postgres(?:ql)?://")),
            // Same portability class MigrationSecurityReviewTests fences on
            // the migration digest: an assembly file name is stable across
            // machines, so the byte-diff can't catch it either.
            ("assembly artifact", new Regex(@"[\w.-]+\.(?:dll|pdb)\b", RegexOptions.IgnoreCase)),
        };

        var failures = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(DocsDir, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var (name, pattern) in leaks)
            {
                var m = pattern.Match(text);
                if (m.Success)
                    failures.AppendLine(
                        $"{Path.GetRelativePath(RepoRoot, file)}: {name}: \"{m.Value}\"");
            }
        }

        Assert.True(failures.Length == 0, "Environment leaked into generated docs:\n" + failures);
    }

    // Completeness, measured against the real migrated database rather than a
    // hand-kept list: every table, every index (by name AND full definition —
    // expression indexes and partial predicates ride along in
    // pg_get_indexdef's text), and every check constraint present in a virgin
    // migrated schema must appear in the committed docs.
    [Fact]
    public async Task CommittedSchemaDocs_CoverEveryTableIndexAndCheckConstraint()
    {
        Assert.True(Directory.Exists(DocsDir),
            "docs/schema/ does not exist — run tools/schema-docs/generate.sh");

        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());
        await db.Database.MigrateAsync();

        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var missing = new StringBuilder();
        // All checks are scoped to the object's OWNING page, and name+def are
        // required on ONE line (a markdown table row). Whole-docs containment
        // was refutable two ways: a name surviving in the page's Indexes
        // section while its Constraints row is gone, and a generic definition
        // ("PRIMARY KEY (\"Id\")") borrowed from any other table's page.
        var pageLines = new Dictionary<string, string[]>();
        string[] LinesOf(string table)
        {
            var page = Path.Combine(DocsDir, $"public.{table}.md");
            if (!pageLines.TryGetValue(page, out var lines))
                pageLines[page] = lines = File.Exists(page) ? File.ReadAllLines(page) : [];
            return lines;
        }
        // BIDIRECTIONAL per-section comparison (like the column check): the
        // complete row set of the page's section must equal the catalog's
        // set for that table — a live object without its row fails, and so
        // does a phantom or duplicate row PostgreSQL doesn't have, which a
        // per-object existence check could never reject. Rows are matched in
        // the kind's OWN section (a misclassified row is a failure), and the
        // definition must be an exact cell of the name's row.
        void RequireSection(string table, string heading, List<(string Table, string Name, string Def)> catalogRows)
        {
            var lines = LinesOf(table);
            if (lines.Length == 0) return; // missing page already reported
            var inSection = false;
            var docRows = new List<string[]>();
            foreach (var line in lines)
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                    inSection = line.TrimEnd().Equals(heading, StringComparison.Ordinal);
                else if (inSection && line.StartsWith("| ", StringComparison.Ordinal))
                    docRows.Add(line.Split('|').Select(c => c.Trim()).ToArray());
            }
            var docNames = docRows.Skip(2).Select(cells => cells[1]).ToList(); // header + separator skipped
            var docSorted = docNames.OrderBy(n => n, StringComparer.Ordinal).ToList();
            var catalogSorted = catalogRows.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (!docSorted.SequenceEqual(catalogSorted, StringComparer.Ordinal))
            {
                missing.AppendLine(
                    $"{heading} row set mismatch in public.{table}.md:\n" +
                    $"  docs:    [{string.Join(", ", docSorted)}]\n" +
                    $"  catalog: [{string.Join(", ", catalogSorted)}]");
                return; // per-row checks would just repeat the mismatch
            }
            foreach (var (_, name, def) in catalogRows)
            {
                // The definition must sit in its DESIGNATED column — the
                // last cell of the row in both section shapes — not merely
                // anywhere in the row (a Type/Definition cell swap would
                // otherwise pass).
                var ok = docRows.Skip(2).Any(cells =>
                    cells.Length > 2 && cells[1] == name && cells[^2] == def);
                if (!ok)
                    missing.AppendLine($"row definition mismatch in the {heading} section of public.{table}.md: {name} — {def}");
            }
        }

        var tables = await QueryStringsAsync(conn,
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename");

        // Column-level completeness, scoped to the page's "## Columns"
        // section: every page also contains header rows like "| Name | Type |",
        // so a page-wide cell search would accept an omitted column that
        // happens to be named like a header (Customers.Name). Only the data
        // rows of the Columns table count — and the row's METADATA cells are
        // held to the catalog exactly: type, default, and nullability. The
        // type comparison uses format_type with tbls's single observed
        // normalization (character varying → varchar; every other type cell
        // across all 38 tables is format_type verbatim) — a future type this
        // mapping doesn't cover fails loudly and gets added consciously,
        // which beats both silently trusting the cell and mirroring tbls's
        // whole normalizer.
        var columnsByTable = (await QueryColumnsAsync(conn)).GroupBy(c => c.Table).ToList();
        foreach (var tableColumns in columnsByTable)
        {
            var lines = LinesOf(tableColumns.Key);
            if (lines.Length == 0) continue; // missing page already reported
            var inColumns = false;
            var dataRows = new List<string[]>();
            foreach (var line in lines)
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                    inColumns = line.StartsWith("## Columns", StringComparison.Ordinal);
                else if (inColumns && line.StartsWith("| ", StringComparison.Ordinal))
                    dataRows.Add(line.Split('|').Select(c => c.Trim()).ToArray());
            }
            // First two pipe rows are the header and its separator; cell 0 is
            // the empty prefix before the leading pipe.
            var docRows = dataRows.Skip(2).Where(cells => cells.Length > 4).ToList();

            // ORDERED sequence comparison, not per-column lookup: physical
            // column order is part of the documented contract (format.sort:
            // false — the catalog shows the real database), so an
            // alphabetized rendering must fail even though every column is
            // still present. SequenceEqual also rejects phantom rows for
            // columns the catalog doesn't have.
            var docOrder = docRows.Select(cells => cells[1]).ToList();
            var catalogOrder = tableColumns.Select(c => c.Column).ToList();
            if (!docOrder.SequenceEqual(catalogOrder, StringComparer.Ordinal))
            {
                missing.AppendLine(
                    $"column sequence mismatch in public.{tableColumns.Key}.md:\n" +
                    $"  docs:    [{string.Join(", ", docOrder)}]\n" +
                    $"  catalog: [{string.Join(", ", catalogOrder)}]");
                continue; // per-cell checks would just repeat the mismatch
            }

            foreach (var (col, row) in tableColumns.Zip(docRows))
            {
                var expectedType = col.Type.Replace("character varying", "varchar");
                if (row[2] != expectedType)
                    missing.AppendLine($"column type mismatch in public.{col.Table}.md: {col.Column} — docs \"{row[2]}\", catalog \"{expectedType}\"");
                if (row[3] != col.Default)
                    missing.AppendLine($"column default mismatch in public.{col.Table}.md: {col.Column} — docs \"{row[3]}\", catalog \"{col.Default}\"");
                if (row[4] != col.Nullable)
                    missing.AppendLine($"column nullability mismatch in public.{col.Table}.md: {col.Column} — docs \"{row[4]}\", catalog \"{col.Nullable}\"");
            }
        }

        var indexesByTable = (await QueryTriplesAsync(conn,
            """
            SELECT tablename, indexname, indexdef FROM pg_indexes
            WHERE schemaname = 'public'
            ORDER BY indexname
            """)).GroupBy(r => r.Table).ToDictionary(g => g.Key, g => g.ToList());

        // EVERY table-backed constraint row, no contype allow-list: a
        // hand-kept type list is the exact shape #407 warns about — an
        // exclusion constraint (or any future kind) added by a migration
        // must fail this test if tbls stops documenting it, not slide
        // through a filter written before it existed. The pg_class join
        // already restricts the sweep to constraints owned by public tables.
        var constraintsByTable = (await QueryTriplesAsync(conn,
            """
            SELECT rel.relname, con.conname, pg_get_constraintdef(con.oid)
            FROM pg_constraint con
            JOIN pg_class rel ON rel.oid = con.conrelid
            JOIN pg_namespace ns ON ns.oid = rel.relnamespace
            WHERE ns.nspname = 'public'
            ORDER BY con.conname
            """)).GroupBy(r => r.Table).ToDictionary(g => g.Key, g => g.ToList());

        // The UNION of catalog owners, not just pg_tables: an indexed
        // materialized view contributes to pg_indexes but not pg_tables, so
        // iterating tables alone would skip its section entirely. (No such
        // relation exists in this schema today — this is the same
        // future-proof shape as the partitioned-table relkind widening.)
        // Page EXISTENCE is checked over the same union — a union-only owner
        // (an indexed materialized view) whose page is entirely omitted must
        // be reported here, since RequireSection's empty-page early return
        // relies on this loop having said so.
        var ownerUnion = tables
            .Union(indexesByTable.Keys, StringComparer.Ordinal)
            .Union(constraintsByTable.Keys, StringComparer.Ordinal)
            // Column owners too: an unindexed, unconstrained relation (a
            // bare materialized view) appears ONLY in the column query, and
            // its omitted page must still be reported here — the column
            // loop's empty-page continue relies on it.
            .Union(columnsByTable.Select(g => g.Key), StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        foreach (var table in ownerUnion)
        {
            if (!File.Exists(Path.Combine(DocsDir, $"public.{table}.md")))
            {
                missing.AppendLine($"relation without a doc page: {table}");
                continue;
            }
            RequireSection(table, "## Indexes", indexesByTable.GetValueOrDefault(table) ?? []);
            RequireSection(table, "## Constraints", constraintsByTable.GetValueOrDefault(table) ?? []);
        }

        // BIDIRECTIONAL at the page level too: a committed public.<x>.md for
        // a relation the live catalog doesn't have documents a phantom —
        // and --check reproduces it, so only this comparison can see it.
        foreach (var page in Directory.EnumerateFiles(DocsDir, "public.*.md")
            .Select(f => Path.GetFileName(f)!)
            .Select(f => f["public.".Length..^".md".Length])
            .Where(rel => !ownerUnion.Contains(rel, StringComparer.Ordinal))
            .OrderBy(r => r, StringComparer.Ordinal))
        {
            missing.AppendLine($"doc page without a catalog relation: {page}");
        }

        // The README's flagship ERD is held to the catalog too: every
        // relation must appear as an entity block and every FK as an edge
        // (mermaid renders the pg_get_constraintdef text with #quot;
        // entity-escaped quotes) — per-relation pages surviving while the
        // diagram silently drops entities/edges is a drift --check
        // reproduces faithfully.
        // BIDIRECTIONAL multiset comparison for the ERD, like every other
        // level: missing, phantom, AND duplicate entities/edges all fail.
        // Edges are (source entity, label) pairs — identical FK defs exist
        // on sibling tables (AspNetRoleClaims and AspNetUserRoles both
        // reference AspNetRoles via RoleId), so the label alone cannot
        // identify an edge; that surviving mutant forced the pairing.
        var readme = File.ReadAllText(Path.Combine(DocsDir, "README.md"));
        var readmeLines = readme.Split('\n');
        var erdEntities = readmeLines
            .Select(l => Regex.Match(l.TrimEnd(), @"^""public\.([^""]+)""[ \t]*\{$"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (!erdEntities.SequenceEqual(ownerUnion, StringComparer.Ordinal))
        {
            missing.AppendLine(
                "README ERD entity set does not match the catalog:\n" +
                $"  erd:     [{string.Join(", ", erdEntities)}]\n" +
                $"  catalog: [{string.Join(", ", ownerUnion)}]");
        }
        // The tuple carries source, TARGET, and label: a redirected edge —
        // right label, wrong drawn target — must not pass, so the target
        // parsed from the diagram is compared against the table the
        // definition actually REFERENCES.
        var erdEdges = readmeLines
            .Select(l => Regex.Match(l.TrimEnd(), @"^""public\.([^""]+)""[ \t]+\S+[ \t]+""public\.([^""]+)""[ \t]*:[ \t]*""(.+)""$"))
            .Where(m => m.Success)
            .Select(m => $"{m.Groups[1].Value} :: {m.Groups[2].Value} :: {m.Groups[3].Value}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        var expectedEdges = constraintsByTable
            .SelectMany(kv => kv.Value
                .Where(r => r.Def.StartsWith("FOREIGN KEY", StringComparison.Ordinal))
                .Select(r =>
                {
                    var target = Regex.Match(r.Def, @"REFERENCES ""([^""]+)""").Groups[1].Value;
                    return $"{kv.Key} :: {target} :: {r.Def.Replace("\"", "#quot;")}";
                }))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (!erdEdges.SequenceEqual(expectedEdges, StringComparer.Ordinal))
        {
            missing.AppendLine(
                "README ERD FK edge multiset does not match the catalog:\n" +
                $"  erd-only:     [{string.Join(" | ", erdEdges.Except(expectedEdges))}]\n" +
                $"  catalog-only: [{string.Join(" | ", expectedEdges.Except(erdEdges))}]\n" +
                $"  (counts: erd {erdEdges.Count}, catalog {expectedEdges.Count})");
        }

        Assert.True(missing.Length == 0,
            "Migrated schema contains objects the committed docs do not:\n" + missing +
            "\nRegenerate with tools/schema-docs/generate.sh");
    }

    private static async Task<List<string>> QueryStringsAsync(
        System.Data.Common.DbConnection conn, string sql)
    {
        var results = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) results.Add(reader.GetString(0));
        return results;
    }

    private static async Task<List<(string Name, string Def)>> QueryPairsAsync(
        System.Data.Common.DbConnection conn, string sql)
    {
        var results = new List<(string, string)>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) results.Add((reader.GetString(0), reader.GetString(1)));
        return results;
    }

    private static async Task<List<(string Table, string Column, string Type, string Default, string Nullable)>>
        QueryColumnsAsync(System.Data.Common.DbConnection conn)
    {
        var results = new List<(string, string, string, string, string)>();
        await using var cmd = conn.CreateCommand();
        // Defaults and nullability are rendered by tbls verbatim
        // (pg_get_expr text; "true"/"false"); types via format_type.
        cmd.CommandText =
            """
            SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod),
                   COALESCE(pg_get_expr(d.adbin, d.adrelid), ''),
                   CASE WHEN a.attnotnull THEN 'false' ELSE 'true' END
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'm', 'v', 'f')
              AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY c.relname, a.attnum
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4)));
        return results;
    }

    private static async Task<List<(string Table, string Name, string Def)>> QueryTriplesAsync(
        System.Data.Common.DbConnection conn, string sql)
    {
        var results = new List<(string, string, string)>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return results;
    }
}
