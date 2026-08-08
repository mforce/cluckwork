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
            ("absolute unix path", new Regex(@"(?m)(?:^|[\s(""'`=])/[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]*)*")),
            ("absolute windows path", new Regex(@"(?i)[a-z]:\\")),
            ("timestamp", new Regex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}")),
            ("connection URI (would carry the ephemeral password)", new Regex(@"postgres(?:ql)?://")),
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
        void RequireRow(string table, string name, string def, string kind)
        {
            var lines = LinesOf(table);
            if (lines.Length == 0) return; // missing page already reported
            if (!lines.Any(l => l.Contains($"| {name} |", StringComparison.Ordinal)
                    && l.Contains(def, StringComparison.Ordinal)))
                missing.AppendLine($"{kind} row absent from public.{table}.md: {name} — {def}");
        }

        foreach (var table in await QueryStringsAsync(conn,
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename"))
        {
            if (!File.Exists(Path.Combine(DocsDir, $"public.{table}.md")))
                missing.AppendLine($"table without a doc page: {table}");
        }

        // Column-level completeness, scoped to the page's "## Columns"
        // section: every page also contains header rows like "| Name | Type |",
        // so a page-wide cell search would accept an omitted column that
        // happens to be named like a header (Customers.Name). Only the data
        // rows of the Columns table count.
        foreach (var row in await QueryPairsAsync(conn,
            """
            SELECT table_name, column_name FROM information_schema.columns
            WHERE table_schema = 'public'
            ORDER BY table_name, ordinal_position
            """))
        {
            var lines = LinesOf(row.Name);
            if (lines.Length == 0) continue; // missing page already reported
            var inColumns = false;
            var dataRows = new List<string>();
            foreach (var line in lines)
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                    inColumns = line.StartsWith("## Columns", StringComparison.Ordinal);
                else if (inColumns && line.StartsWith("| ", StringComparison.Ordinal))
                    dataRows.Add(line);
            }
            // First two pipe rows are the header and its separator.
            var cells = dataRows.Skip(2).Select(l => l.Split('|')[1].Trim()).ToHashSet(StringComparer.Ordinal);
            if (!cells.Contains(row.Def))
                missing.AppendLine($"column absent from the Columns section of public.{row.Name}.md: {row.Def}");
        }

        foreach (var row in await QueryTriplesAsync(conn,
            """
            SELECT tablename, indexname, indexdef FROM pg_indexes
            WHERE schemaname = 'public'
            ORDER BY indexname
            """))
        {
            RequireRow(row.Table, row.Name, row.Def, "index");
        }

        // Every constraint kind: PK ('p'), unique ('u'), FK ('f' —
        // pg_get_constraintdef carries the ON DELETE action the issue
        // requires documented), check ('c'), and Postgres 18's named NOT
        // NULL constraints ('n'), which tbls documents as constraint rows.
        foreach (var row in await QueryTriplesAsync(conn,
            """
            SELECT rel.relname, con.conname, pg_get_constraintdef(con.oid)
            FROM pg_constraint con
            JOIN pg_class rel ON rel.oid = con.conrelid
            JOIN pg_namespace ns ON ns.oid = rel.relnamespace
            WHERE ns.nspname = 'public' AND con.contype IN ('p', 'u', 'f', 'c', 'n')
            ORDER BY con.conname
            """))
        {
            RequireRow(row.Table, row.Name, row.Def, "constraint");
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
