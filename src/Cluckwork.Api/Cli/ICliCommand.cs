namespace Cluckwork.Api.Cli;

using Microsoft.AspNetCore.Builder;

// A one-off, run-then-exit operator command on the API binary. Dispatched from
// Program.cs immediately after Build() and
// BEFORE the web host starts — Kestrel and the hosted services never run for
// these. Extracted from Program.cs (#288) so each command is an isolated,
// testable unit rather than another `if (args[0] == "…")` block wedged into the
// bootstrapper.
public interface ICliCommand
{
    // The verb that selects this command (args[0]).
    string Name { get; }

    // Runs the command against the built host and returns the process exit code.
    Task<int> RunAsync(WebApplication app, string[] args);
}
