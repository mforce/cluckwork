namespace Cluckwork.Api.IntegrationTests;

using System.Text.RegularExpressions;

// #273 codex review (round 5) — BootstrapAdminCommandTests and
// RecoverAdminCommandTests prove the "never the logger" invariant by scanning
// the SUBPROCESS'S OWN captured stdout for the generated password. Once #349's
// redaction pipeline is wired in, that scan stops being able to prove it: a
// regression that logs the password through a forbidden-named property (e.g.
// `logger.LogInformation("... {TemporaryPassword}", outcome.TemporaryPassword)`)
// would have it rewritten to "[REDACTED]" before it ever reaches the captured
// Console sink output — producing stdout IDENTICAL to correct code. A dynamic,
// post-hoc, black-box text scan cannot tell "never touched ILogger" apart from
// "touched ILogger, then redacted".
//
// This is therefore a STATIC, source-level guard instead of a dynamic one: it
// reads the CLI command source files directly (never executes them) and
// asserts the generated-password property is referenced EXACTLY ONCE in each
// named secret-printing file, on a line that calls Console.Out and nothing that looks like a logger
// call. A new reference anywhere else in either file — in particular a second
// reference on a logger.Log* line — fails this test immediately, regardless of
// what the redaction pipeline would have done to it downstream.
//
// Deliberately narrow in scope, matching what the tests it supplements actually
// covered: the CLI command boundary, not the full call graph down through
// FirstRunAdminService/AdminRecoveryService/IdentityProvider. Widening further
// would mean parsing C# rather than grepping it, which is a different, heavier
// tool for a marginal gain here — TemporaryPassword.Generate() and its callers
// pass the raw string as a parameter, never format it into a log template
// (verified by reading, at review time, and worth re-reading before trusting
// this guard's boundary if that code changes).
public sealed class SecretCliLoggingGuardTests
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

    // Matches any call that looks like it logs through Microsoft.Extensions.Logging
    // (logger.LogInformation/.../LogCritical, or the generic Log<TState> overload) —
    // deliberately broad rather than an exact method-name allowlist, so a new
    // logging extension method added later still trips this.
    private static readonly Regex LoggerCallPattern = new(
        @"\blogger\.\w*Log\w*\s*\(", RegexOptions.Compiled);

    [Theory]
    [InlineData("src/Cluckwork.Api/Cli/BootstrapAdminCliCommand.cs", "TemporaryPassword")]
    [InlineData("src/Cluckwork.Api/Cli/RecoverAdminCliCommand.cs", "TemporaryPassword")]
    // Keep this list explicit: deriving it from all ICliCommand implementations
    // would force "exactly once" into "at most once", allowing a secret-printing
    // command to stop printing its generated password without failing the guard.
    [InlineData("src/Cluckwork.Api/Cli/ProvisionAccountCliCommand.cs", "TemporaryPassword")]
    public void The_generated_password_is_referenced_exactly_once_and_only_via_ConsoleOut(
        string relativePath, string propertyName)
    {
        var path = Path.Combine(RepositoryRoot, relativePath);
        var source = File.ReadAllText(path);

        var occurrences = Regex.Matches(source, $@"\b{Regex.Escape(propertyName)}\b");
        Assert.True(occurrences.Count == 1,
            $"{relativePath}: expected exactly one reference to {propertyName}, found " +
            $"{occurrences.Count}. Every additional reference must be individually justified — this " +
            "guard exists precisely because it is NOT safe to assume the redaction pipeline will " +
            "catch a stray logging call.");

        var line = LineContaining(source, occurrences[0].Index);
        Assert.Contains("Console.Out", line);
        Assert.False(LoggerCallPattern.IsMatch(line),
            $"{relativePath}: the sole reference to {propertyName} sits on a line that also looks " +
            "like a logger call — the password must reach only Console.Out, never ILogger, " +
            "redaction pipeline or not.");
    }

    private static string LineContaining(string source, int index)
    {
        var lineStart = source.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var lineEnd = source.IndexOf('\n', index);
        if (lineEnd < 0) lineEnd = source.Length;
        return source[lineStart..lineEnd];
    }
}
