var builder = DistributedApplication.CreateBuilder(args);

// Local host ports come from `LocalPorts:*`. appsettings.json pins the
// defaults every clone gets so copied psql/redis-cli/browser URLs survive a
// restart; override per machine with user-secrets, per shell with an
// environment variable, or per run with a `--LocalPorts:Api=8080` argument.
//
// Absent, empty and unparseable all resolve to null, which is Aspire's own
// behaviour of assigning a random free host port. That is the escape hatch for
// a machine where a pinned port is taken — pass an empty value — and it means a
// stray value degrades instead of failing the launch. AppHostModelTests relies
// on it to stay machine-independent.
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
