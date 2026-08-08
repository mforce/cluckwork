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
        var pinPattern = new Regex(@"postgres:[0-9][^@\s""']*@sha256:[0-9a-f]{64}");
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
            foreach (Match m in pinPattern.Matches(text))
            {
                if (!hits.TryGetValue(m.Value, out var files))
                    hits[m.Value] = files = [];
                files.Add(relative);
            }
        }

        Assert.True(hits.Count > 0, "No postgres image pin found anywhere — the sweep itself is broken.");
        Assert.True(hits.Count == 1,
            "Multiple distinct postgres image pins found:\n" + string.Join("\n",
                hits.Select(kv => $"  {kv.Key}\n    in: {string.Join(", ", kv.Value.Distinct())}")));
        Assert.Equal(PostgresImage, hits.Keys.Single());
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
        var docs = string.Join("\n",
            Directory.EnumerateFiles(DocsDir, "*.md", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(File.ReadAllText));

        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());
        await db.Database.MigrateAsync();

        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var missing = new StringBuilder();

        foreach (var table in await QueryStringsAsync(conn,
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename"))
        {
            if (!File.Exists(Path.Combine(DocsDir, $"public.{table}.md")))
                missing.AppendLine($"table without a doc page: {table}");
        }

        // Column-level completeness: a page existing does not prove it lists
        // every column. Each column must appear as a cell of its own table's
        // page (the "| Name |" form tbls emits), so an omitted column can't
        // hide behind the same word appearing in prose elsewhere.
        var pageCache = new Dictionary<string, string>();
        foreach (var row in await QueryPairsAsync(conn,
            """
            SELECT table_name, column_name FROM information_schema.columns
            WHERE table_schema = 'public'
            ORDER BY table_name, ordinal_position
            """))
        {
            var page = Path.Combine(DocsDir, $"public.{row.Name}.md");
            if (!File.Exists(page)) continue; // already reported above
            if (!pageCache.TryGetValue(page, out var content))
                pageCache[page] = content = File.ReadAllText(page);
            if (!content.Contains($"| {row.Def} |", StringComparison.Ordinal))
                missing.AppendLine($"column absent from its table page: {row.Name}.{row.Def}");
        }

        foreach (var row in await QueryPairsAsync(conn,
            "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' ORDER BY indexname"))
        {
            if (!docs.Contains(row.Name, StringComparison.Ordinal))
                missing.AppendLine($"index name absent from docs: {row.Name}");
            if (!docs.Contains(row.Def, StringComparison.Ordinal))
                missing.AppendLine($"index definition absent from docs: {row.Def}");
        }

        // Every constraint kind, not just checks: PK ('p'), unique ('u'),
        // FK ('f' — pg_get_constraintdef carries the ON DELETE action the
        // issue requires documented), and check ('c').
        foreach (var row in await QueryPairsAsync(conn,
            """
            SELECT con.conname, pg_get_constraintdef(con.oid)
            FROM pg_constraint con
            JOIN pg_class rel ON rel.oid = con.conrelid
            JOIN pg_namespace ns ON ns.oid = rel.relnamespace
            WHERE ns.nspname = 'public' AND con.contype IN ('p', 'u', 'f', 'c')
            ORDER BY con.conname
            """))
        {
            if (!docs.Contains(row.Name, StringComparison.Ordinal))
                missing.AppendLine($"constraint name absent from docs: {row.Name}");
            if (!docs.Contains(row.Def, StringComparison.Ordinal))
                missing.AppendLine($"constraint definition absent from docs: {row.Def}");
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
}
