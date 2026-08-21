namespace Cluckwork.Api.Cli;

using Microsoft.AspNetCore.Builder;

// Routes args[0] to a one-off ICliCommand. Program.cs calls TryRunAsync right
// after Build(): a non-null result is the command's exit code (return it and
// never start the web host); null means no CLI verb matched, so this is a normal
// serving start. Adding a command is one line in Commands below — no new branch
// in Program.cs (#288).
public static class CliDispatcher
{
    // internal (not private) so a fast test can assert every verb is registered
    // — a dropped entry here would otherwise silently start the web host instead
    // of running the command, caught only by the slow subprocess tests (#288 review).
    internal static readonly ICliCommand[] Commands =
    [
        new SeedCliCommand(),
        new MigrateCliCommand(),
        new RecoverAdminCliCommand(),
        new BootstrapAdminCliCommand(),
        new ListAccountsCliCommand(),
        new SuspendAccountCliCommand(),
        new ReactivateAccountCliCommand(),
    ];

    // Whether these args dispatch to a one-off verb rather than start the web
    // host is answered by ProcessRoles.From(args) (Hosting/ProcessRole.cs), which
    // reads Commands above so the two can't disagree. It deliberately does NOT
    // live here: this dispatcher only knows the four verbs that run AFTER
    // Build(), and `healthcheck` — dispatched before the host exists — is a
    // one-shot process too. A predicate built from Commands alone called it
    // Serving (#347).

    public static async Task<int?> TryRunAsync(WebApplication app, string[] args)
    {
        if (args.Length == 0)
            return null;
        var command = Array.Find(Commands, c => c.Name == args[0]);
        return command is null ? null : await command.RunAsync(app, args);
    }

    // Minimal `--flag value` lookup shared by the commands. Deliberately tiny:
    // the ops verbs take a handful of flags each, so a full CLI parser
    // (System.CommandLine / Spectre.Console.Cli) would be dependency + ceremony
    // out of proportion to the need — a future call if the command set grows.
    internal static string? ArgValue(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == flag)
                return args[i + 1];
        return null;
    }
}
