var builder = DistributedApplication.CreateBuilder(args);

// Local host ports are opt-in and per-machine. Aspire assigns a random free
// host port per run when a port is not stated, which is the default here and
// what docs/runbooks/aspire-local-development.md describes; set a `LocalPorts:*`
// value (user-secrets, an environment variable, or a `--LocalPorts:Api=8080`
// argument) to pin one so copied psql/redis-cli/browser URLs survive a restart.
//
// Absent, empty and unparseable all resolve to null — the dynamic default —
// rather than throwing, so a stray value cannot fail the launch, and a caller
// can force the dynamic case by passing an empty value regardless of what the
// machine's user-secrets hold. AppHostModelTests relies on that to stay
// machine-independent.
int? LocalPort(string name) =>
    int.TryParse(builder.Configuration[$"LocalPorts:{name}"], out var port) ? port : null;

var postgres = builder.AddPostgres("postgres")
    .WithImageTag(ContainerImages.PostgresTag)
    .WithDataVolume("cluckwork-apphost-postgres-pg18")
    .WithImageSHA256(ContainerImages.PostgresSha256)
    .WithHostPort(LocalPort("Postgres"));
var database = postgres.AddDatabase("database", "cluckwork");

var redis = builder.AddRedis("redis")
    .WithImageTag(ContainerImages.RedisTag)
    .WithImageSHA256(ContainerImages.RedisSha256)
    .WithHostPort(LocalPort("Redis"));

var api = builder.AddProject<Projects.Cluckwork_Api>("api")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithHttpEndpoint(name: "http", port: LocalPort("Api"))
    .WithReference(database, connectionName: "Default")
    .WithEnvironment(
        "SharedState__Redis__ConnectionString",
        redis.Resource.ConnectionStringExpression)
    .WaitFor(postgres)
    .WaitFor(redis)
    .WithHttpHealthCheck("/health/live")
    .WithHttpHealthCheck("/health/ready");

// AddViteApp takes no port argument, so the endpoint it already declared is
// mutated in place rather than a second one being added.
builder.AddViteApp("web", "../../web")
    .WithEndpoint("http", endpoint => endpoint.Port = LocalPort("Web"))
    .WithReference(api)
    .WithEnvironment("VITE_API_TARGET", api.GetEndpoint("http"))
    .WaitFor(api);

builder.Build().Run();
