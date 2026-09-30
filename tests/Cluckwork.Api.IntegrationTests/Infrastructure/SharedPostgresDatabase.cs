namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers;
using Cluckwork.Infrastructure.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

internal sealed class SharedPostgresDatabase : IAsyncDisposable
{
    private static readonly Lazy<Task<Cluster>> SharedCluster = new(StartClusterAsync);
    private readonly Lazy<Task<string>> connectionStringTask;
    private readonly bool migrated;

    public SharedPostgresDatabase(bool migrated = false)
    {
        this.migrated = migrated;
        connectionStringTask = new(CreateAsync);
    }

    public Task StartAsync() => connectionStringTask.Value;

    public string GetConnectionString() => connectionStringTask.IsValueCreated && connectionStringTask.Value.IsCompletedSuccessfully
        ? connectionStringTask.Value.Result
        : throw new InvalidOperationException("The test database has not started.");

    public async ValueTask DisposeAsync()
    {
        if (!connectionStringTask.IsValueCreated) return;
        var connectionString = await connectionStringTask.Value;
        await (await SharedCluster.Value).DropAsync(new NpgsqlConnectionStringBuilder(connectionString).Database!);
    }

    private async Task<string> CreateAsync()
    {
        var cluster = await SharedCluster.Value;
        return await cluster.CreateAsync(migrated);
    }

    private static async Task<Cluster> StartClusterAsync()
    {
        const string image = "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";
        var container = new PostgreSqlBuilder(image).Build();
        await container.StartAsync();
        var cluster = new Cluster(container);
        await cluster.PrepareTemplateAsync();
        return cluster;
    }

    private sealed class Cluster(PostgreSqlContainer container)
    {
        private const string TemplateName = "cluckwork_test_template";

        public async Task PrepareTemplateAsync()
        {
            await ExecuteAdminAsync($"CREATE DATABASE \"{TemplateName}\" TEMPLATE template0");
            var templateConnection = ConnectionStringFor(TemplateName, pooled: false);
            var options = new DbContextOptionsBuilder<AppDbContext>();
            new PostgresDbContextConfigurator().Configure(
                options, templateConnection, new DatabaseResilienceOptions());
            options.AddInterceptors(new TenantStampInterceptor(new TenantContext()));
            await using var db = new AppDbContext(options.Options, new TenantContext(), new FlockScope());
            await db.Database.MigrateAsync();
        }

        public async Task<string> CreateAsync(bool migrated)
        {
            var name = $"cluckwork_test_{Guid.NewGuid():N}";
            var source = migrated ? TemplateName : "template0";
            await ExecuteAdminAsync($"CREATE DATABASE \"{name}\" TEMPLATE \"{source}\"");
            return ConnectionStringFor(name);
        }

        public Task DropAsync(string name) =>
            ExecuteAdminAsync($"DROP DATABASE \"{name}\" WITH (FORCE)");

        private string ConnectionStringFor(string name, bool pooled = true) =>
            new NpgsqlConnectionStringBuilder(container.GetConnectionString())
            {
                Database = name,
                Pooling = pooled,
            }.ConnectionString;

        private async Task ExecuteAdminAsync(string sql)
        {
            var adminConnection = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
            {
                Pooling = false,
            }.ConnectionString;
            await using var connection = new NpgsqlConnection(adminConnection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
