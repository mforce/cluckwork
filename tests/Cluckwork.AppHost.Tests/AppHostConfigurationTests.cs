using Aspire.Hosting.Testing;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Cluckwork.AppHost.Tests;

/// <summary>
/// Guards the committed AppHost <c>appsettings.json</c>. It pins the
/// <c>LocalPorts</c> defaults every clone gets, so the values have to stay
/// parseable, mutually distinct, and clear of the ports
/// <c>deploy/docker-compose.dev.yml</c> already publishes.
/// </summary>
public sealed class AppHostConfigurationTests
{
    private static readonly string[] LocalPortKeys = ["Postgres", "Redis", "Api", "Web"];

    [Fact]
    public void Committed_appsettings_pins_every_local_port_to_a_usable_value()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(AppSettingsPath()),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        Assert.Equal(
            ["LocalPorts"],
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());

        var localPorts = document.RootElement.GetProperty("LocalPorts");
        Assert.Equal(
            LocalPortKeys.OrderBy(key => key, StringComparer.Ordinal),
            localPorts.EnumerateObject().Select(property => property.Name).OrderBy(key => key, StringComparer.Ordinal));

        var assigned = new Dictionary<int, string>();
        foreach (var property in localPorts.EnumerateObject())
        {
            Assert.Equal(JsonValueKind.String, property.Value.ValueKind);

            var raw = property.Value.GetString();
            Assert.True(
                int.TryParse(raw, out var port),
                $"LocalPorts:{property.Name} must be a port number, but was '{raw}'. An unparseable value silently falls back to a random port.");

            // Above the privileged range, below the ephemeral range Aspire and
            // Docker draw random host ports from.
            Assert.InRange(port, 1024, 32767);

            // Both of these belong to deploy/docker-compose.dev.yml, which
            // keeps its own data volume. Reusing one points this stack at the
            // other stack's database, or fails the launch outright.
            Assert.False(
                port is 5432 or 6379,
                $"LocalPorts:{property.Name} is {port}, which collides with deploy/docker-compose.dev.yml.");

            Assert.False(
                assigned.TryGetValue(port, out var owner),
                $"LocalPorts:{property.Name} reuses port {port}, already taken by LocalPorts:{owner}.");
            assigned[port] = property.Name;
        }
    }

    [Fact]
    public void AppHost_project_copies_appsettings_to_the_output_directory()
    {
        // Microsoft.NET.Sdk, unlike the Web SDK, has no default item for
        // appsettings.json. Running from source still resolves it through the
        // content root, so dropping the copy does not fail the other tests here
        // -- it fails later and elsewhere, once the AppHost runs from published
        // output or from a different working directory. This asserts the item
        // directly because no behavioural test in this project observes it.
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot(), "src", "Cluckwork.AppHost", "Cluckwork.AppHost.csproj"));

        var copied = project
            .Descendants()
            .Where(element => element.Name.LocalName is "Content" or "None")
            .Where(element => string.Equals(
                element.Attribute("Include")?.Value, "appsettings.json", StringComparison.Ordinal))
            .ToArray();

        var item = Assert.Single(copied);
        var copyToOutput =
            item.Attribute("CopyToOutputDirectory")?.Value
            ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "CopyToOutputDirectory")?.Value;

        Assert.True(
            copyToOutput is "PreserveNewest" or "Always",
            $"appsettings.json must be copied to the output directory, but CopyToOutputDirectory was '{copyToOutput ?? "<unset>"}'.");
    }

    [Fact]
    public async Task Committed_appsettings_is_a_live_configuration_source()
    {
        // No command-line overrides here, unlike AppHostModelTests: the keys
        // must be readable from configuration, which is what proves the file is
        // both copied and parsed (its comments included).
        await using var builder =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.Cluckwork_AppHost>();

        foreach (var key in LocalPortKeys)
        {
            Assert.True(
                builder.Configuration[$"LocalPorts:{key}"] is not null,
                $"LocalPorts:{key} was not readable from AppHost configuration.");
        }
    }

    private static string AppSettingsPath() =>
        Path.Combine(RepositoryRoot(), "src", "Cluckwork.AppHost", "appsettings.json");

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cluckwork.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root from the test output directory.");
    }
}
