using System.Text.RegularExpressions;

namespace Cluckwork.Application.Tests.Documentation;

// #1278 G3.21: an enrolled user's failed login carries a sign-in challenge,
// which is a credential, so a harness that signs in reports a failure by the
// response's status and problem title and nothing else of the response. The
// walk reads every tracked file under tools/simulation and tools/verify that
// signs in, finds each value bound to an HTTP response or its parsed body, and
// checks every failure message and error argument that reads one.
public sealed partial class HarnessLoginBodyTests
{
    // On 2026-10-29 nine tracked files there named the login route, and their
    // failure reports read a response in ten places.
    private const int LoginFileFloor = 4;
    private const int ResponseReadFloor = 4;

    private static readonly string[] SignsIn = ["/auth/login", "AUTH_PATHS.LOGIN"];

    // What a failure may read off a response: its status (code in Python's
    // HTTPError) and its problem title.
    private static readonly HashSet<string> AllowedMembers = ["status", "code", "title"];

    // Values bound to a response or a parsed body, by the HTTP and JSON entry
    // points the three harness languages use.
    [GeneratedRegex(@"(?:const|let|var)?\s*(\w+)\s*=\s*(?:await\s+)?(?:http\.\w+|fetch|safeJson|urllib\.request\.urlopen|json\.loads|\w+\.json)\(")]
    private static partial Regex ResponseBinding();

    [GeneratedRegex(@"(?:except\s+[\w.]+\s+as|with\s+urllib\.request\.urlopen\(.*?\)\s+as)\s+(\w+)")]
    private static partial Regex ResponseAlias();

    // A failure report: a thrown error, an error log, or a print to stderr.
    [GeneratedRegex(@"throw new \w+\(|console\.error\(|print\(")]
    private static partial Regex FailureCall();

    // jq interpolation in a runbook's fallback string.
    [GeneratedRegex(@"\\\(\.(\w+)\)")]
    private static partial Regex JqField();

    [Fact]
    public void AHarnessThatSignsIn_ReportsOnlyAResponsesStatusAndTitle()
    {
        var root = TenancyDocsFreshnessTests.RepoRoot();
        var files = TenancyDocsFreshnessTests.TrackedFiles(root)
            .Where(p => p.StartsWith("tools/simulation/", StringComparison.Ordinal) || p.StartsWith("tools/verify/", StringComparison.Ordinal))
            .Select(p => (Path: p, Text: File.ReadAllText(Path.Combine(root, p))))
            .Where(f => SignsIn.Any(route => f.Text.Contains(route, StringComparison.Ordinal)))
            .ToList();
        Assert.True(files.Count >= LoginFileFloor, $"the walk found {files.Count} files that sign in, below its floor of {LoginFileFloor}");

        var reads = files.SelectMany(f => ResponseReads(f.Text).Select(r => (Where: $"{f.Path}: {r.Read}", r.Allowed))).ToList();

        Assert.True(reads.Count >= ResponseReadFloor, $"the walk found {reads.Count} failure reads of a response, below its floor of {ResponseReadFloor}");
        var offenders = reads.Where(r => !r.Allowed).Select(r => r.Where).ToList();
        Assert.True(offenders.Count == 0,
            "a harness that signs in reports more of a response than its status and title:\n  " + string.Join("\n  ", offenders));
    }

    // Each read of a response inside a failure report, and whether it is a
    // bare truthiness guard or one of the allowed members.
    private static IEnumerable<(string Read, bool Allowed)> ResponseReads(string text)
    {
        var responses = ResponseBinding().Matches(text).Select(m => m.Groups[1].Value)
            .Concat(ResponseAlias().Matches(text).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var call in FailureCalls(text))
            foreach (var name in responses)
                foreach (Match use in Regex.Matches(call, $@"(?<![\w.])(len\()?{Regex.Escape(name)}\b(\.\w+|\s*&&)?"))
                {
                    // A count, a truthiness guard, or an allowed member.
                    var member = use.Groups[2].Value;
                    var allowed = use.Groups[1].Success || member.TrimStart() == "&&"
                        || (member.StartsWith('.') && AllowedMembers.Contains(member[1..]));
                    yield return (use.Value.Trim(), allowed);
                }

        foreach (var line in text.Split('\n').Where(l => SignsIn.Any(route => l.Contains(route, StringComparison.Ordinal))))
            foreach (Match field in JqField().Matches(line))
                yield return (field.Value, AllowedMembers.Contains(field.Groups[1].Value));
    }

    private static IEnumerable<string> FailureCalls(string text)
    {
        foreach (Match start in FailureCall().Matches(text))
        {
            var depth = 0;
            for (var i = start.Index + start.Length - 1; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')' && --depth == 0)
                {
                    var call = text[start.Index..(i + 1)];
                    if (!call.StartsWith("print(", StringComparison.Ordinal) || call.Contains("sys.stderr", StringComparison.Ordinal))
                        yield return call;
                    break;
                }
            }
        }
    }
}
