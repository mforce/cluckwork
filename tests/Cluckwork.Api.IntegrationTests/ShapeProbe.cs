namespace Cluckwork.Api.IntegrationTests;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

// #512 US4 — the instrument behind the query-shape guards: a DbCommandInterceptor
// that COUNTS the SQL the production reads already carry, and does nothing else.
//
// The reads are tagged in the repositories (ReferenceMarkers); this type only
// observes. It never rewrites command text and is never registered by production
// DI — NamedRowProjectionFactory registers it, and nothing else does — so every
// other suite, and every real request, sends the same bytes to Postgres as it
// would without this file.
//
// WHAT THE TAG LOOKS LIKE TO THIS INTERCEPTOR. EF's TagWith folds tags into a
// LEADING comment block, and that block IS part of the CommandText an interceptor
// at ReaderExecutingAsync is handed — measured across this suite's four reads, the
// tag is present in every observed CommandText and stripping leading comments
// contributes nothing to matching. So `HasTag` is a plain Contains on the raw text,
// and the recorded Body is comment-stripped only so a shape assertion ("LEFT JOIN",
// a projection list) reads the statement without the tag block in front of it. The
// two are deliberately different views of the same command: match the tag on what
// the server received, assert the shape on the statement proper.
//
// Because the tag's arrival is a framework behaviour rather than this repo's
// contract, the instrument's own honesty is asserted: a guard whose instrument
// silently sees nothing would report "exactly one read" while measuring zero, which
// is the worst possible guard failure — it reads as a pass.
// ProbeSeesEveryTaggedRead exists to make that failure red instead, and it is the
// reason every tag-keyed count in NamedRowProjectionTests is allowed to mean
// anything.
public sealed class ShapeProbe : DbCommandInterceptor
{
    // Mirrors Cluckwork.Infrastructure.Repositories.ReferenceMarkers. The test
    // assembly cannot see an internal of Infrastructure, so the literals are
    // repeated here; ProbeSeesEveryTaggedRead reddens a drift between
    // the two copies, so the duplication is guarded rather than trusted.
    public const string FlockReference = "cluckwork-flock-reference";
    public const string CustomerReference = "cluckwork-customer-reference";
    public const string MovementAggregate = "cluckwork-movement-aggregate";
    public const string AssignmentProjection = "cluckwork-assignment-projection";

    private Window? _active;

    // Instance-based, and that is a requirement rather than a style choice. The
    // interceptor is a singleton per WebApplicationFactory, and xunit parallelises
    // CLASSES — so other suites' factories exist alongside this one and other
    // classes' requests flow through their own interceptors. A process-wide static
    // "current probe" gets overwritten by whichever factory was constructed last,
    // and this suite then arms a probe no request passes through: every count reads
    // zero and every "exactly one read" guard passes while measuring nothing. Each
    // test reaches its own factory's probe instead, which is the one its requests
    // actually go through.
    public Window Arm(string tag)
    {
        var window = new Window(tag, this);
        // Arming replaces whatever window was open. That is deliberate — a test that
        // forgets to close one must not leak its statements into the next one's
        // counts — but it also means an unclosed window is invisible, so the
        // disposing side closes conditionally (see Window.Dispose).
        Interlocked.Exchange(ref _active, window);
        return window;
    }

    // Closes the window only if it is still the open one. A test that opens two (two
    // page sizes, say) and disposes the first AFTER arming the second would otherwise
    // close the second early and silently lose its statements — a missing count reads
    // as "the read ran zero times", which a `Single` assertion catches, but only by
    // accident, and an accidental failure is a future flake.
    internal bool CloseIfCurrent(Window window) =>
        Interlocked.CompareExchange(ref _active, null, window) == window;

    // Unconditionally closes, for the lifetime-end case where no specific window is
    // at hand (the test class's DisposeAsync).
    public void Disarm() => Volatile.Write(ref _active, null);

    // One observation window. Statements are recorded UNFILTERED so a single
    // window can answer both "how often did the tagged read run" (Marked) and
    // "was this projection one statement or two" (Statements) — a tag-matching
    // capture could not answer the second, since an untagged-looking statement
    // matches nothing by definition.
    public sealed class Window(string tag, ShapeProbe owner) : IDisposable
    {
        internal string TagOf() => tag;

        // The parameter VALUES bound into each TAGGED statement, in the same order
        // as Marked. A read's CONTRACT is often "bounded to the keys of this page",
        // and the honest way to assert a bound is the set of values actually sent: it
        // holds whatever operator the provider chose (`= ANY`, `IN`, a join), and a
        // bound that silently bound nothing shows up as a count that does not match.
        //
        // Tagged-only, not one list per observed statement: the window records EVERY
        // statement — that is what lets a guard notice an untagged second round trip
        // — so a bound assertion has to address the read it is about rather than
        // whatever else the request happened to run (a middleware credential-epoch
        // read, for one).
        public IReadOnlyList<string> MarkedParameters
        {
            get { lock (_entries) return _entries.Where(e => e.HasTag).Select(e => e.Parameters).ToList(); }
        }

        public IReadOnlyList<int> MarkedParameterCounts
        {
            get { lock (_entries) return _entries.Where(e => e.HasTag).Select(e => e.ParameterCount).ToList(); }
        }

        // Retires this window: statements recorded afterwards belong to no one.
        // Arming a new window does the same; Dispose is the explicit case — a guard
        // that opens two windows in one test (two page sizes, say) closes the first
        // before arming the second so neither count absorbs the other's statements.
        public void Dispose() => owner.CloseIfCurrent(this);

        private readonly List<Entry> _entries = [];

        internal void Add(Entry entry)
        {
            lock (_entries) _entries.Add(entry);
        }

        // Statements observed in this window, comments stripped — what a shape
        // assertion ("LEFT JOIN", a projection list) should run against. This list is
        // deliberately NOT tag-filtered: an N+1 fallback would not carry the tag, so a
        // guard asking "did anything else ride along?" has to see everything.
        public IReadOnlyList<string> Statements
        {
            get { lock (_entries) return _entries.Select(e => e.Body).ToList(); }
        }

        // The statements in this window that carry the armed tag, i.e. the
        // executions of the one read under measurement.
        public IReadOnlyList<string> Marked
        {
            get
            {
                lock (_entries)
                    return _entries.Where(e => e.HasTag).Select(e => e.Body).ToList();
            }
        }
    }

        // `Parameters` is the flattened bound values as text; `ParameterCount` counts
        // DISTINCT values, since a null-valued or empty parameter contributes no bound.
        internal readonly record struct Entry(string Body, bool HasTag, string Parameters, int ParameterCount);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Observe(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Observe(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Observe(DbCommand command)
    {
        var window = Volatile.Read(ref _active);
        if (window is null) return;

        var text = command.CommandText;
        // .ToList() FIRST: iterating command.Parameters while enumerating lazily
        // detaches the collection on the first MoveNext, so a second read of it sees
        // an empty set — which would make every "how many ids were bound" assertion
        // compare against nothing and pass vacuously.
        // Only an ARRAY parameter is a bounded id set. A scalar (a tenant id, a
        // timestamp, a page size) is not part of any bound, and counting it as one
        // would make "the bound equals the page's keys" depend on how many unrelated
        // scalars the request happened to carry.
        var values = command.Parameters.Cast<DbParameter>()
            .Where(static p => p.Value is System.Collections.IEnumerable
                && p.Value is not string)
            .Select(static p => ParameterText.Of(p.Value))
            .Where(static v => v.Length > 0)
            .ToList();
        var flattened = ParameterValues.Flattened(values).ToList();
        // The tag is matched on the RAW text, which is what the server receives: EF's
        // tag block is part of CommandText (see the class comment). Body is stripped
        // separately, for shape assertions.
        window.Add(new Entry(
            Body: StripLeadingComments(text),
            HasTag: text.Contains(window.TagOf(), StringComparison.Ordinal),
            Parameters: string.Join("\n", flattened),
            ParameterCount: flattened.Distinct(StringComparer.Ordinal).Count()));
    }

    // Drops a run of leading `-- …` / `/* … */` lines — where EF puts its tag block —
    // so a shape assertion sees the statement. NOT part of tag matching.
    internal static string StripLeadingComments(string sql)
    {
        var text = sql;
        while (true)
        {
            var trimmed = text.TrimStart();
            if (trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                var newline = trimmed.IndexOf('\n');
                if (newline < 0) return "";
                text = trimmed[(newline + 1)..];
                continue;
            }
            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                var close = trimmed.IndexOf("*/", StringComparison.Ordinal);
                if (close < 0) return "";
                text = trimmed[(close + 2)..];
                continue;
            }
            return trimmed;
        }
    }
}

// Value formats the way the provider chooses; an array of Guids is the case that
// matters here, and its elements are what a bounded read is bounded TO.
internal static class ParameterText
{
    public static string Of(object? value) => value switch
    {
        null => "",
        System.Collections.IEnumerable en and not string => "{"
            + string.Join(", ", en.Cast<object>().Select(static e => Format(e))) + "}",
        _ => value is Guid g2 ? g2.ToString("D") : (value.ToString() ?? ""),
    };

    // Npgsql materialises a Guid[] parameter as a UUID array whose elements may
    // arrive as Guid or as string depending on the pipeline; both formats are
    // compared as the "D" form so a caller can match a Guid.ToString("D").
    private static string Format(object? element) => element switch
    {
        Guid g => g.ToString("D"),
        _ => element?.ToString() ?? "",
    };
}

// A Guid[] array parameter arrives as ONE DbParameter whose Value is an Array, and
// its text form is a comma-joined brace list. A bound's worth is its ELEMENTS, so
// flatten one level; anything not brace-list shaped is a single value.
internal static class ParameterValues
{
    public static IEnumerable<string> Flattened(IEnumerable<string> values) =>
        values.SelectMany(v => v.StartsWith('{') && v.EndsWith('}')
            ? v.Trim('{', '}').Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            : [v]);
}
