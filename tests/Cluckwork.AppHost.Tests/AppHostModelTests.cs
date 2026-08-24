using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Cluckwork.AppHost.Tests;

public sealed class AppHostModelTests
{
    private const string PostgresReference =
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";
    private const string PostgresTag = "18.4-trixie";
    private const string PostgresSha256 = "3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";
    private const string RedisReference =
        "redis:7.4-alpine@sha256:e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2";
    private const string RedisTag = "7.4-alpine";
    private const string RedisSha256 = "e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2";

    [Fact]
    public void AppHost_launch_profile_is_minimal_and_runs_in_Development()
    {
        var launchSettingsPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Cluckwork.AppHost",
            "Properties",
            "launchSettings.json");

        using var document = JsonDocument.Parse(File.ReadAllText(launchSettingsPath));
        var root = document.RootElement;
        Assert.Equal(["profiles"], root.EnumerateObject().Select(property => property.Name).ToArray());

        var profiles = root.GetProperty("profiles");
        Assert.Equal(["http"], profiles.EnumerateObject().Select(property => property.Name).ToArray());

        var profile = profiles.GetProperty("http");
        Assert.Equal(
            ["commandName", "environmentVariables"],
            profile.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal("Project", profile.GetProperty("commandName").GetString());

        var environmentVariables = profile.GetProperty("environmentVariables");
        Assert.True(
            environmentVariables.TryGetProperty("DOTNET_ENVIRONMENT", out var dotnetEnvironment),
            "AppHost launch profile must set DOTNET_ENVIRONMENT.");
        Assert.True(
            environmentVariables.TryGetProperty("ASPNETCORE_ENVIRONMENT", out var aspnetcoreEnvironment),
            "AppHost launch profile must set ASPNETCORE_ENVIRONMENT.");
        Assert.Equal(
            ["ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT"],
            environmentVariables.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal("Development", dotnetEnvironment.GetString());
        Assert.Equal("Development", aspnetcoreEnvironment.GetString());
    }

    [Fact]
    public async Task Model_declares_the_exact_expected_resources_annotations_and_relationships()
    {
        await using var builder = await CreateBuilderAsync();

        Assert.Equal(
            ["api", "api-rebuilder", "database", "postgres", "redis", "web", "web-installer"],
            builder.Resources.Select(resource => resource.Name).OrderBy(name => name).ToArray());

        var postgres = GetResource(builder.Resources, "postgres");
        var database = GetResource(builder.Resources, "database");
        var redis = GetResource(builder.Resources, "redis");
        var api = GetResource(builder.Resources, "api");
        var web = GetResource(builder.Resources, "web");
        var apiRebuilder = GetResource(builder.Resources, "api-rebuilder");
        var webInstaller = GetResource(builder.Resources, "web-installer");

        AssertEndpoints(postgres, new EndpointExpectation("tcp", "tcp", null, 5432));
        AssertEndpoints(redis, new EndpointExpectation("tcp", "redis", null, 6379));
        AssertEndpoints(api, new EndpointExpectation("http", "http", null, null));
        AssertEndpoints(web, new EndpointExpectation("http", "http", null, null));
        AssertEndpoints(database);
        AssertEndpoints(apiRebuilder);
        AssertEndpoints(webInstaller);

        AssertHealthChecks(postgres, "postgres_check");
        AssertHealthChecks(database, "database_check");
        AssertHealthChecks(redis, "redis_check");
        AssertHealthChecks(api, "api_http_/health/live_200_check", "api_http_/health/ready_200_check");
        AssertHealthChecks(web);
        AssertHealthChecks(apiRebuilder);
        AssertHealthChecks(webInstaller);

        AssertWaits(
            api,
            new WaitExpectation("postgres", WaitType.WaitUntilHealthy),
            new WaitExpectation("redis", WaitType.WaitUntilHealthy));
        AssertWaits(
            web,
            new WaitExpectation("api", WaitType.WaitUntilHealthy),
            new WaitExpectation("web-installer", WaitType.WaitForCompletion));
        AssertWaits(postgres);
        AssertWaits(database);
        AssertWaits(redis);
        AssertWaits(apiRebuilder);
        AssertWaits(webInstaller);

        AssertReferenceRelationships(api, "database", "redis", "redis-password");
        AssertReferenceRelationships(web, "api", "api");
        AssertReferenceRelationships(postgres);
        AssertReferenceRelationships(database);
        AssertReferenceRelationships(redis);
        AssertReferenceRelationships(apiRebuilder);
        AssertReferenceRelationships(webInstaller);
    }

    [Fact]
    public async Task Local_port_configuration_pins_every_host_port()
    {
        await using var builder = await CreateBuilderAsync(
            "--LocalPorts:Postgres=15432",
            "--LocalPorts:Redis=16379",
            "--LocalPorts:Api=18080",
            "--LocalPorts:Web=15173");

        AssertEndpoints(
            GetResource(builder.Resources, "postgres"),
            new EndpointExpectation("tcp", "tcp", 15432, 5432));
        AssertEndpoints(
            GetResource(builder.Resources, "redis"),
            new EndpointExpectation("tcp", "redis", 16379, 6379));
        AssertEndpoints(
            GetResource(builder.Resources, "api"),
            new EndpointExpectation("http", "http", 18080, null));
        AssertEndpoints(
            GetResource(builder.Resources, "web"),
            new EndpointExpectation("http", "http", 15173, null));
    }

    [Fact]
    public async Task Unparseable_local_port_configuration_falls_back_to_the_dynamic_default()
    {
        await using var builder = await CreateBuilderAsync(
            "--LocalPorts:Postgres=not-a-port",
            "--LocalPorts:Redis=",
            "--LocalPorts:Api=not-a-port",
            "--LocalPorts:Web=");

        AssertEndpoints(
            GetResource(builder.Resources, "postgres"),
            new EndpointExpectation("tcp", "tcp", null, 5432));
        AssertEndpoints(
            GetResource(builder.Resources, "api"),
            new EndpointExpectation("http", "http", null, null));
    }

    [Fact]
    public async Task Container_images_are_exact_pinned_references_with_split_components()
    {
        await using var builder = await CreateBuilderAsync();

        var postgresImage = GetImage(GetResource(builder.Resources, "postgres"));
        Assert.Equal("docker.io", postgresImage.Registry);
        Assert.Equal("library/postgres", postgresImage.Image);
        Assert.Null(postgresImage.Tag);
        Assert.Equal(PostgresSha256, postgresImage.SHA256);
        Assert.Equal(PostgresTag, ContainerImages.PostgresTag);
        Assert.Equal(PostgresSha256, ContainerImages.PostgresSha256);
        Assert.Equal(PostgresReference, $"postgres:{ContainerImages.PostgresTag}@sha256:{ContainerImages.PostgresSha256}");
        Assert.Equal(PostgresReference, ContainerImages.PostgresReference);

        var redisImage = GetImage(GetResource(builder.Resources, "redis"));
        Assert.Equal("docker.io", redisImage.Registry);
        Assert.Equal("library/redis", redisImage.Image);
        Assert.Null(redisImage.Tag);
        Assert.Equal(RedisSha256, redisImage.SHA256);
        Assert.Equal(RedisTag, ContainerImages.RedisTag);
        Assert.Equal(RedisSha256, ContainerImages.RedisSha256);
        Assert.Equal(RedisReference, $"redis:{ContainerImages.RedisTag}@sha256:{ContainerImages.RedisSha256}");
        Assert.Equal(RedisReference, ContainerImages.RedisReference);
    }

    [Fact]
    public async Task Postgres_volume_and_fluent_declaration_order_are_version_safe()
    {
        await using var builder = await CreateBuilderAsync();

        var postgres = GetResource(builder.Resources, "postgres");
        var volume = Assert.Single(postgres.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal(ContainerMountType.Volume, volume.Type);
        Assert.Equal("cluckwork-apphost-postgres-pg18", volume.Source);
        Assert.Equal("/var/lib/postgresql", volume.Target);
        Assert.False(volume.IsReadOnly);

        var redis = GetResource(builder.Resources, "redis");
        Assert.Empty(redis.Annotations.OfType<ContainerMountAnnotation>());

        var program = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Cluckwork.AppHost", "Program.cs"));
        const string postgresChain = "var postgres = builder.AddPostgres(\"postgres\")";
        var postgresStart = Assert.Single(Regex.Matches(program, Regex.Escape(postgresChain)).Cast<Match>()).Index;
        var postgresText = GetSemicolonBoundedChain(program, postgresChain, postgresStart);

        var postgresTag = AssertExactlyOnceInProgramAndChain(
            program,
            postgresText,
            ".WithImageTag(ContainerImages.PostgresTag)");
        var postgresVolume = AssertExactlyOnceInProgramAndChain(
            program,
            postgresText,
            ".WithDataVolume(\"cluckwork-apphost-postgres-pg18\")");
        var postgresSha = AssertExactlyOnceInProgramAndChain(
            program,
            postgresText,
            ".WithImageSHA256(ContainerImages.PostgresSha256)");

        Assert.True(postgresTag > 0, "PostgreSQL must set its image tag in its resource chain.");
        Assert.True(postgresVolume > postgresTag, "PostgreSQL must select the PostgreSQL 18 volume path after its tag.");
        Assert.True(postgresSha > postgresVolume, "PostgreSQL must assign the digest after the tag selected the PostgreSQL 18 volume path.");

        const string redisChain = "var redis = builder.AddRedis(\"redis\")";
        var redisStart = Assert.Single(Regex.Matches(program, Regex.Escape(redisChain)).Cast<Match>()).Index;
        var redisText = GetSemicolonBoundedChain(program, redisChain, redisStart);
        var redisTag = AssertExactlyOnceInProgramAndChain(
            program,
            redisText,
            ".WithImageTag(ContainerImages.RedisTag)");
        var redisSha = AssertExactlyOnceInProgramAndChain(
            program,
            redisText,
            ".WithImageSHA256(ContainerImages.RedisSha256)");

        Assert.True(redisTag > 0, "Redis must set its image tag in its resource chain.");
        Assert.True(redisSha > redisTag, "Redis must set its digest after its tag.");
    }

    [Fact]
    public async Task Api_and_web_model_the_required_expressions()
    {
        await using var builder = await CreateBuilderAsync();

        var api = GetResource(builder.Resources, "api");
        var web = GetResource(builder.Resources, "web");

        var apiEnvironment = await GetPublishEnvironmentAsync(api);
        Assert.Equal("Development", apiEnvironment["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("Development", apiEnvironment["DOTNET_ENVIRONMENT"]);
        Assert.Equal("{database.connectionString}", apiEnvironment["ConnectionStrings__Default"]);
        Assert.Equal(
            "{redis.bindings.tcp.host}:{redis.bindings.tcp.port},password={redis-password.value}{cond-redis-bindings-tcp-tlsenabled-d148d83a.connectionString}",
            apiEnvironment["SharedState__Redis__ConnectionString"]);

        var webEnvironment = await GetPublishEnvironmentAsync(web);
        Assert.Equal("{api.bindings.http.url}", webEnvironment["VITE_API_TARGET"]);
    }

    // The AppHost loads its own user-secrets, so a machine that pins a
    // `LocalPorts:*` value would otherwise change what these tests observe.
    // Command-line arguments outrank user-secrets, and the AppHost treats an
    // empty value as unset, so this forces the dynamic default everywhere.
    private static readonly string[] DynamicPortArguments =
    [
        "--LocalPorts:Postgres=",
        "--LocalPorts:Redis=",
        "--LocalPorts:Api=",
        "--LocalPorts:Web=",
    ];

    private static async Task<IDistributedApplicationTestingBuilder> CreateBuilderAsync(params string[] args) =>
        await DistributedApplicationTestingBuilder.CreateAsync<Projects.Cluckwork_AppHost>(
            args.Length == 0 ? DynamicPortArguments : args);

    private static IResource GetResource(IEnumerable<IResource> resources, string name) =>
        Assert.Single(resources, resource => resource.Name == name);

    private static ContainerImageAnnotation GetImage(IResource resource) =>
        Assert.Single(resource.Annotations.OfType<ContainerImageAnnotation>());

    private static void AssertEndpoints(IResource resource, params EndpointExpectation[] expected)
    {
        var actual = resource.Annotations
            .OfType<EndpointAnnotation>()
            .Select(endpoint => new EndpointExpectation(endpoint.Name, endpoint.UriScheme, endpoint.Port, endpoint.TargetPort))
            .OrderBy(endpoint => endpoint.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.OrderBy(endpoint => endpoint.Name, StringComparer.Ordinal), actual);
    }

    private static void AssertHealthChecks(IResource resource, params string[] expected) =>
        Assert.Equal(
            expected.OrderBy(key => key, StringComparer.Ordinal),
            resource.Annotations.OfType<HealthCheckAnnotation>().Select(annotation => annotation.Key).OrderBy(key => key, StringComparer.Ordinal));

    private static void AssertWaits(IResource resource, params WaitExpectation[] expected)
    {
        var actual = resource.Annotations
            .OfType<WaitAnnotation>()
            .Select(wait => new WaitExpectation(wait.Resource.Name, wait.WaitType))
            .OrderBy(wait => wait.ResourceName, StringComparer.Ordinal)
            .ThenBy(wait => wait.WaitType)
            .ToArray();

        Assert.Equal(
            expected.OrderBy(wait => wait.ResourceName, StringComparer.Ordinal).ThenBy(wait => wait.WaitType),
            actual);
    }

    private static void AssertReferenceRelationships(IResource resource, params string[] expectedDependencies) =>
        Assert.Equal(
            expectedDependencies.OrderBy(name => name, StringComparer.Ordinal),
            resource.Annotations
                .OfType<ResourceRelationshipAnnotation>()
                .Where(annotation => annotation.Type == "Reference")
                .Select(annotation => annotation.Resource.Name)
                .OrderBy(name => name, StringComparer.Ordinal));

    private static string GetSemicolonBoundedChain(string program, string declaration, int start)
    {
        var terminator = program.IndexOf(';', start);
        Assert.True(terminator > start, $"Could not find the terminating semicolon for '{declaration}'.");
        return program[start..(terminator + 1)];
    }

    private static int AssertExactlyOnceInProgramAndChain(string program, string chain, string invocation)
    {
        Assert.Single(Regex.Matches(program, Regex.Escape(invocation)).Cast<Match>());
        var occurrence = chain.IndexOf(invocation, StringComparison.Ordinal);
        Assert.NotEqual(-1, occurrence);
        Assert.Single(Regex.Matches(chain, Regex.Escape(invocation)).Cast<Match>());
        return occurrence;
    }

    private static async Task<IReadOnlyDictionary<string, string>> GetPublishEnvironmentAsync(IResource resource)
    {
        var environmentResource = Assert.IsAssignableFrom<IResourceWithEnvironment>(resource);

#pragma warning disable CS0618 // This Aspire 13.5 model-test helper is the documented public inspection API.
        return await environmentResource.GetEnvironmentVariableValuesAsync(DistributedApplicationOperation.Publish);
#pragma warning restore CS0618
    }

    private static string FindRepositoryRoot()
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

    private sealed record EndpointExpectation(string Name, string UriScheme, int? Port, int? TargetPort);

    private sealed record WaitExpectation(string ResourceName, WaitType WaitType);
}
