namespace Cluckwork.Api.Hosting;

using Cluckwork.Api.Cli;

// #347 — what this process was started to BE, decided once from args before
// anything is registered or built.
//
// The problem it replaces: several boot guards were serving-process-only, and
// what made them so was WHERE their statement sat in Program.cs — after the CLI
// dispatch meant a one-shot verb never reached them. That is not a property
// anyone can read off the guard, so every new guard reopened the question "does
// this apply to `migrate`?", and getting it wrong is not a compile error. #331
// is what getting it wrong looks like: the #316 OTLP endpoint validation ran at
// service registration, ahead of the dispatcher, and killed `recover-admin` —
// the break-glass verb, the one that has to work when everything else is
// broken — with SIGABRT 134.
//
// Now each guard checks the role itself, so its scope survives being moved.
internal enum ProcessRole
{
    // Started to serve HTTP traffic: Kestrel binds, hosted services run.
    Serving,

    // Started to run one operator verb and exit. Kestrel never binds and the
    // hosted services never run, so guards protecting the SERVING process's
    // security posture have nothing to protect here — and a guard that aborts
    // one of these verbs takes out an operational escape hatch instead.
    OneShot,
}

internal static class ProcessRoles
{
    // Every verb whose presence at args[0] makes this a one-shot process.
    //
    // Derived from CliDispatcher.Commands rather than restated, so a sixth verb
    // added to the dispatcher is classified correctly without anyone
    // remembering this file. `healthcheck` cannot come from there: it is not an
    // ICliCommand (it needs no host, so it takes no WebApplication) and is
    // dispatched by Program.cs before the host is built — which is exactly why
    // it is named here explicitly. Leaving it out is not harmless-by-luck: it
    // would classify the container's own health probe as a SERVING process, and
    // the only thing standing between that and a guard abort would once again
    // be a statement's position in Program.cs.
    internal static readonly string[] OneShotVerbs =
        [.. CliDispatcher.Commands.Select(command => command.Name), HealthCheckCliCommand.Verb];

    public static ProcessRole From(string[] args) =>
        args.Length > 0 && Array.IndexOf(OneShotVerbs, args[0]) >= 0
            ? ProcessRole.OneShot
            : ProcessRole.Serving;
}
