using Aspire.Hosting.Testing;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Cluckwork.AppHost.Tests;

/// <summary>
/// Guards the committed AppHost <c>appsettings.json</c>. It pins the
/// <c>LocalPorts</c> defaults every clone gets, so the values have to stay
/// parseable, mutually distinct, and clear of the ports
/// <c>deploy/docker-compose.dev.yml</c> already publishes. It also guards the
/// AppHost's build files.
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
    public void Aspire_sdk_versions_match_every_Aspire_package_pin()
    {
        // Dependabot bumps the PackageVersion pins but has never moved a
        // project's Sdk="Aspire.AppHost.Sdk/x" attribute, and a stale SDK pulls
        // in its own older Aspire.Hosting.AppHost (#1404, #1409).
        var root = RepositoryRoot();

        var pins = XDocument.Load(Path.Combine(root, "Directory.Packages.props"))
            .Descendants()
            .Where(element => element.Name.LocalName == "PackageVersion")
            .Select(element => (Name: element.Attribute("Include")?.Value ?? "", Version: element.Attribute("Version")?.Value))
            .Where(pin => pin.Name.StartsWith("Aspire.", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(pins.Length > 0, "Directory.Packages.props pins no Aspire.* package.");

        // MSBuild takes an SDK from the <Project Sdk="Name/Version"> attribute,
        // an <Sdk Name Version> element, or an <Import Sdk Version> element.
        var sdks = XDocument.Load(Path.Combine(root, "Cluckwork.slnx"))
            .Descendants("Project")
            .Select(project => project.Attribute("Path")!.Value.Replace('\\', '/'))
            .SelectMany(path =>
            {
                var project = XDocument.Load(Path.Combine(root, path)).Root!;
                return (project.Attribute("Sdk")?.Value ?? "")
                    .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(sdk => sdk.Split('/', 2))
                    .Select(parts => (Form: "<Project Sdk> attribute", Name: parts[0], Version: parts.ElementAtOrDefault(1)))
                    .Concat(project.Descendants()
                        .Where(element => element.Name.LocalName == "Sdk")
                        .Select(element => (Form: "<Sdk> element", Name: element.Attribute("Name")?.Value ?? "", Version: element.Attribute("Version")?.Value)))
                    .Concat(project.Descendants()
                        .Where(element => element.Name.LocalName == "Import" && element.Attribute("Sdk") is not null)
                        .Select(element => (Form: "<Import Sdk> element", Name: element.Attribute("Sdk")!.Value, Version: element.Attribute("Version")?.Value)))
                    .Select(sdk => (Project: path, sdk.Form, sdk.Name, sdk.Version));
            })
            .Where(sdk => sdk.Name.StartsWith("Aspire.", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(
            sdks.Any(sdk => string.Equals(sdk.Name, "Aspire.AppHost.Sdk", StringComparison.OrdinalIgnoreCase)),
            "No project in Cluckwork.slnx declares the Aspire.AppHost.Sdk SDK.");

        // The guard reads only versions written inline. A version moved to
        // global.json or a <Version> child would otherwise compare null to null.
        var unread = sdks
            .Where(sdk => string.IsNullOrWhiteSpace(sdk.Version))
            .Select(sdk => $"{sdk.Project} names {sdk.Name} in its {sdk.Form} with no inline version, so this guard cannot read it.")
            .Concat(pins
                .Where(pin => string.IsNullOrWhiteSpace(pin.Version))
                .Select(pin => $"Directory.Packages.props pins {pin.Name} with no Version attribute, so this guard cannot read it."))
            .ToArray();
        Assert.True(unread.Length == 0, string.Join(Environment.NewLine, unread));

        var drift = sdks
            .SelectMany(sdk => pins
                .Where(pin => !string.Equals(pin.Version, sdk.Version, StringComparison.OrdinalIgnoreCase))
                .Select(pin => $"{sdk.Project} uses {sdk.Name} {sdk.Version} in its {sdk.Form}, but Directory.Packages.props pins {pin.Name} at {pin.Version}."))
            .ToArray();
        Assert.True(
            drift.Length == 0,
            string.Join(Environment.NewLine, drift)
            + Environment.NewLine
            + "Update each reported Aspire SDK declaration to the matching package version, in the same PR. Dependabot updates the pins but not the SDK declarations. If no Aspire.AppHost.Sdk of that version is published, decide by hand whether to pin the packages back or accept the gap.");
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
            if (File.Exists(Path.Combine(directory.FullName, "Cluckwork.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root from the test output directory.");
    }
}
