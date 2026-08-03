namespace Cluckwork.Api.IntegrationTests;

// #364 — the credential gate's position is a security property, not an
// incidental startup detail. Runtime endpoint tests pin the observable edges;
// this source-level fence pins the complete sequence so adding/reordering a
// middleware cannot silently leave an untested gap between those edges.
public sealed class CredentialEpochMiddlewareOrderTests
{
    [Fact]
    public void Program_PinsTheCompleteCredentialGateSequence()
    {
        var repository = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(
            repository.FullName, "src", "Cluckwork.Api", "Program.cs"));
        string[] expectedOrder =
        [
            "app.UseAuthentication();",
            "app.UseMiddleware<TenantResolutionMiddleware>();",
            "app.UseMiddleware<CredentialEpochMiddleware>();",
            "app.UseMiddleware<MustChangePasswordMiddleware>();",
            "app.UseAuthorization();",
            "app.UseMiddleware<IdempotencyMiddleware>();",
        ];

        var previous = -1;
        foreach (var statement in expectedOrder)
        {
            var position = program.IndexOf(statement, StringComparison.Ordinal);
            Assert.True(position >= 0, $"Program.cs is missing required middleware statement: {statement}");
            Assert.True(position > previous,
                $"Program.cs middleware is out of order at: {statement}");
            previous = position;
        }
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cluckwork.sln")))
                return directory;
        }

        throw new DirectoryNotFoundException("Could not locate the Cluckwork repository root.");
    }
}
