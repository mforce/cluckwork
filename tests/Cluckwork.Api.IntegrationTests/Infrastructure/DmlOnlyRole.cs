namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using System.Security.Cryptography;
using Npgsql;

internal static class DmlOnlyRole
{
    public static async Task<string> CreateConnectionStringAsync(string adminConnectionString)
    {
        var roleName = $"dml_test_role_{Guid.NewGuid():N}";
        var rolePassword = RandomNumberGenerator.GetHexString(64);

        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"""
                CREATE ROLE "{roleName}" LOGIN PASSWORD '{rolePassword}';
                GRANT USAGE ON SCHEMA public TO "{roleName}";
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO "{roleName}";
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO "{roleName}";
                """;
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Username = roleName,
            Password = rolePassword,
        }.ConnectionString;
    }
}
