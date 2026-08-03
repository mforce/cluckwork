namespace Cluckwork.Api.IntegrationTests;

// #364 — the credential gate's position is a security property, not an
// incidental startup detail. Runtime endpoint tests pin the observable edges;
// this source-level fence pins the complete contiguous sequence so adding,
// duplicating, or reordering middleware cannot silently leave an untested gap.
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

        var first = program.IndexOf(expectedOrder[0], StringComparison.Ordinal);
        var last = program.IndexOf(expectedOrder[^1], StringComparison.Ordinal);
        Assert.True(first >= 0, $"Program.cs is missing required middleware statement: {expectedOrder[0]}");
        Assert.True(last > first, $"Program.cs is missing or misorders: {expectedOrder[^1]}");

        foreach (var statement in expectedOrder)
        {
            Assert.Equal(1, CountOccurrences(program, statement));
        }

        var securitySlice = program[first..(last + expectedOrder[^1].Length)];
        var actualOrder = securitySlice.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("app.Use", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(expectedOrder, actualOrder);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = source.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
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
