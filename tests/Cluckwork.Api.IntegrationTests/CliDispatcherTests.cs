namespace Cluckwork.Api.IntegrationTests;

using System.Linq;
using Cluckwork.Api.Cli;

// #288 — the extracted CLI dispatcher. Each command is covered end-to-end by its
// own subprocess test (SeedCommandTests, MigrateCommandTests,
// RecoverAdminCommandTests); these cover the ROUTING itself. No Docker.
public sealed class CliDispatcherTests
{
    // A dropped/renamed registry entry would make that verb silently start the
    // web host instead of running the command — a regression otherwise caught
    // only by the slow subprocess tests (#288 review). Assert the registry holds
    // exactly the known verbs, with no duplicate Names.
    [Fact]
    public void Registry_ContainsEveryVerb_WithNoDuplicateNames()
    {
        var names = CliDispatcher.Commands.Select(c => c.Name).ToArray();
        Assert.Equal(["bootstrap-admin", "migrate", "recover-admin", "seed"], names.OrderBy(n => n).ToArray());
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    // No-arg and unknown-verb both return null (= "not a command; start the web
    // host"). Passing null! for the host is sound ONLY because both paths return
    // before any `app` access — TryRunAsync inspects args before touching the host.
    [Fact]
    public async Task TryRun_NoArgs_ReturnsNull_SoTheWebHostStarts()
    {
        Assert.Null(await CliDispatcher.TryRunAsync(null!, []));
    }

    [Fact]
    public async Task TryRun_UnknownVerb_ReturnsNull_SoTheWebHostStarts()
    {
        Assert.Null(await CliDispatcher.TryRunAsync(null!, ["not-a-command", "--flag", "v"]));
    }
}
