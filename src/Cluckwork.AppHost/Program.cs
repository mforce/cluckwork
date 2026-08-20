var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithImageTag(ContainerImages.PostgresTag)
    .WithDataVolume("cluckwork-apphost-postgres-pg18")
    .WithImageSHA256(ContainerImages.PostgresSha256);
var database = postgres.AddDatabase("database", "cluckwork");

var redis = builder.AddRedis("redis")
    .WithImageTag(ContainerImages.RedisTag)
    .WithImageSHA256(ContainerImages.RedisSha256);

var api = builder.AddProject<Projects.Cluckwork_Api>("api")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithHttpEndpoint(name: "http")
    .WithReference(database, connectionName: "Default")
    .WithEnvironment(
        "SharedState__Redis__ConnectionString",
        redis.Resource.ConnectionStringExpression)
    .WaitFor(postgres)
    .WaitFor(redis)
    .WithHttpHealthCheck("/health/live")
    .WithHttpHealthCheck("/health/ready");

builder.AddViteApp("web", "../../web")
    .WithReference(api)
    .WithEnvironment("VITE_API_TARGET", api.GetEndpoint("http"))
    .WaitFor(api);

builder.Build().Run();
