namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.Cli;
using Cluckwork.Api.Hosting;

// #347 — the verb registry ProcessRoles.From reads. Fast, no host, no container.
//
// This suite is NOT the guarantee: proving that a serving-only guard leaves a
// one-shot verb alone needs the real binary, and that is ProcessRoleGuardTests.
// What this covers is the classification itself, and one case the subprocess
// suite structurally cannot reach — `healthcheck` returns before the host is
// ever built, so no boot guard can observe its role today. Which is precisely
// why the classification needs pinning here: the only thing keeping the health
// probe safe would otherwise be, once again, where a statement sits in
// Program.cs.
public sealed class ProcessRoleRegistryTests
{
    [Fact]
    public void EveryDispatchedVerb_IsAOneShotProcess()
    {
        // Walk the dispatcher's own registry rather than a list written here: a
        // sixth verb must be classified without anyone remembering this test.
        foreach (var command in CliDispatcher.Commands)
            Assert.Equal(ProcessRole.OneShot, ProcessRoles.From([command.Name]));
    }

    // Not an ICliCommand and not dispatched by CliDispatcher, so a predicate
    // derived from Commands alone classifies it Serving — the latent bug #347
    // found. Named explicitly here because that is the only way it can be wrong.
    [Fact]
    public void Healthcheck_IsAOneShotProcess()
    {
        Assert.Equal(ProcessRole.OneShot, ProcessRoles.From([HealthCheckCliCommand.Verb]));
        Assert.Contains(HealthCheckCliCommand.Verb, ProcessRoles.OneShotVerbs);
    }

    [Fact]
    public void AVerbCarryingItsOwnFlags_IsStillAOneShotProcess()
    {
        Assert.Equal(
            ProcessRole.OneShot,
            ProcessRoles.From(["recover-admin", "--email", "owner@example.test", "--reason", "drill"]));
    }

    [Fact]
    public void AnythingElse_IsAServingProcess()
    {
        Assert.Equal(ProcessRole.Serving, ProcessRoles.From([]));                        // plain serving start
        Assert.Equal(ProcessRole.Serving, ProcessRoles.From(["not-a-verb"]));            // unknown first argument
        Assert.Equal(ProcessRole.Serving, ProcessRoles.From(["--urls", "http://+:8080"])); // host arguments, no verb
        Assert.Equal(ProcessRole.Serving, ProcessRoles.From(["--profile", "migrate"]));  // a verb NAME, not at args[0]
    }
}
