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
            // #532 — between authentication and tenant resolution, and the
            // position is the point. It blanks the ambient principal for
            // /auth/login, so a bearer for one farm cannot resolve a tenant
            // while the request authenticates against another. Placed AFTER
            // tenant resolution it would be useless (the tenant is already
            // resolved from the foreign bearer, and AccessFailedAsync then
            // writes across the boundary, bypassing the #128 lockout); placed
            // BEFORE authentication it would have no principal to blank.
            "app.UseMiddleware<AmbientPrincipalMiddleware>();",
            "app.UseMiddleware<TenantResolutionMiddleware>();",
            "app.UseMiddleware<FlockScopeResolutionMiddleware>();",
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
        var actualOrder = ExecutableStatements(securitySlice);
        Assert.Equal(expectedOrder, actualOrder);
    }

    // #388 — a DB fault raised while resolving flock scope must still reach
    // the mapped ProblemDetails response, not an unhandled-exception 500. That
    // only holds if the exception handler wraps the flock-scope middleware,
    // i.e. is registered earlier in the pipeline.
    [Fact]
    public void Program_ExceptionHandlerWrapsFlockScopeResolution()
    {
        var repository = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(
            repository.FullName, "src", "Cluckwork.Api", "Program.cs"));

        const string exceptionHandler = "app.UseExceptionHandler(";
        const string flockScope = "app.UseMiddleware<FlockScopeResolutionMiddleware>();";

        Assert.Equal(1, CountOccurrences(program, exceptionHandler));
        Assert.Equal(1, CountOccurrences(program, flockScope));

        var exceptionHandlerIndex = program.IndexOf(exceptionHandler, StringComparison.Ordinal);
        var flockScopeIndex = program.IndexOf(flockScope, StringComparison.Ordinal);

        Assert.True(exceptionHandlerIndex >= 0,
            $"Program.cs is missing required middleware statement: {exceptionHandler}");
        Assert.True(flockScopeIndex >= 0,
            $"Program.cs is missing required middleware statement: {flockScope}");
        Assert.True(exceptionHandlerIndex < flockScopeIndex,
            "app.UseExceptionHandler(...) must be registered before " +
            "app.UseMiddleware<FlockScopeResolutionMiddleware>() so a DB fault " +
            "raised while resolving flock scope reaches the mapped ProblemDetails " +
            "response instead of surfacing as an unhandled 500.");
    }

    [Fact]
    public void FenceIncludesAnyInterveningExecutableRegistration()
    {
        const string source = """
            app.UseAuthentication();
            // A comment is harmless.
            app.MapWhen(_ => true, branch => branch.Run(_ => Task.CompletedTask));
            app.UseAuthorization();
            """;

        Assert.Equal(
        [
            "app.UseAuthentication();",
            "app.MapWhen(_ => true, branch => branch.Run(_ => Task.CompletedTask));",
            "app.UseAuthorization();",
        ], ExecutableStatements(source));
    }

    private static string[] ExecutableStatements(string source) => source.Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
        .ToArray();

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
