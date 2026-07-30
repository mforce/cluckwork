namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.Cli;

// #288 — the extracted CLI dispatcher. Each command is covered end-to-end by its
// own subprocess test (SeedCommandTests, MigrateCommandTests,
// RecoverAdminCommandTests); this covers the ROUTING, which decides whether the
// process is a one-off command or a normal serving start. A wrong "null" here
// would silently start the web host for what should be a command (or vice-versa).
// No Docker: the null-match paths return before the host is touched.
public sealed class CliDispatcherTests
{
    [Fact]
    public async Task TryRun_NoArgs_ReturnsNull_SoTheWebHostStarts()
    {
        Assert.Null(await CliDispatcher.TryRunAsync(null!, System.Array.Empty<string>()));
    }

    [Fact]
    public async Task TryRun_UnknownVerb_ReturnsNull_SoTheWebHostStarts()
    {
        Assert.Null(await CliDispatcher.TryRunAsync(null!, ["not-a-command", "--flag", "v"]));
    }
}
