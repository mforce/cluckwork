namespace Cluckwork.Api.IntegrationTests;

using System.Text.RegularExpressions;

// #775 — the four test projects are legs of ci.yml's `tests` matrix, so that a failing
// group cancels the others instead of letting them burn their runner time, and so that
// the 791 fast tests report in about ninety seconds rather than behind the 1,793
// Testcontainers ones.
//
// One `dotnet test Cluckwork.sln` could not leave a project unrun. A matrix CAN, and it
// fails silently: a new test project simply never executes, and every check stays green
// while nothing ran it. That is the failure this guard exists for.
//
// It therefore walks the SOLUTION rather than a remembered list — the "walk everything,
// exclude deliberately" shape AGENTS.md prefers — and asks of each test project whether
// ci.yml names it anywhere runnable. It matches the PATH, not the command, so it stays
// indifferent to arrangement: the matrix could become four jobs again, or collapse back
// into one step, and the question it asks is unchanged.
public sealed class SolutionTestProjectSplitTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string WorkflowPath = Path.Combine(".github", "workflows", "ci.yml");

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cluckwork.sln")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the Cluckwork repository root.");
    }

    // Project("{guid}") = "Name", "tests/Name/Name.csproj", "{guid}"
    private static readonly Regex SolutionProjectPattern = new(
        @"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]+"",\s*""([^""]+\.csproj)""",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // `Tests` without a preceding dot, because the projects do not agree on the shape:
    // three end `.Tests` and the fourth is `Cluckwork.Api.IntegrationTests`.
    private static readonly Regex TestProjectPathPattern = new(
        @"tests/[A-Za-z0-9.]+Tests\b", RegexOptions.Compiled);

    private static IReadOnlyList<string> SolutionTestProjectDirectories() =>
        [.. SolutionProjectPattern
            .Matches(File.ReadAllText(Path.Combine(RepositoryRoot, "Cluckwork.sln")))
            .Select(match => match.Groups[1].Value.Replace('\\', '/'))
            .Where(path => path.StartsWith("tests/", StringComparison.Ordinal))
            .Select(path => Path.GetDirectoryName(path)!.Replace('\\', '/'))];

    // Comment lines are dropped BEFORE matching, and that is load-bearing rather than
    // tidiness. The prose above the `tests` job names project paths while explaining the
    // split; counted as workflow content, those mentions alone would satisfy every
    // assertion below and the guard would report safety while running nothing. This drops
    // any line whose first non-blank character is `#`, which covers YAML comments and
    // shell comments inside a `run:` block alike. A real step is never on such a line.
    private static string RunnableWorkflowText() =>
        string.Join('\n', File.ReadAllLines(Path.Combine(RepositoryRoot, WorkflowPath))
            .Where(line => !line.TrimStart().StartsWith('#')));

    private static IReadOnlyList<string> ProjectsNamedByWorkflow() =>
        [.. TestProjectPathPattern.Matches(RunnableWorkflowText())
            .Select(match => match.Value)
            .Distinct()];

    [Fact]
    public void EveryTestProjectInTheSolution_IsNamedByTheWorkflow()
    {
        var projects = SolutionTestProjectDirectories();
        var named = ProjectsNamedByWorkflow();

        // A walk that found nothing would pass the loop below while proving nothing at
        // all, so both inputs have to be non-empty before they are compared.
        Assert.NotEmpty(projects);
        Assert.NotEmpty(named);

        foreach (var project in projects)
            Assert.True(named.Contains(project),
                $"{project} is a test project in Cluckwork.sln, but {WorkflowPath} never names it "
                + $"outside a comment, so its tests never run and CI stays green regardless. Add it "
                + $"as a leg of the `tests` matrix. Named there: {string.Join(", ", named)}");
    }

    [Fact]
    public void EveryProjectTheWorkflowNames_Exists()
    {
        var named = ProjectsNamedByWorkflow();
        Assert.NotEmpty(named);

        foreach (var project in named)
            Assert.True(Directory.Exists(Path.Combine(RepositoryRoot, project)),
                $"{WorkflowPath} names {project} as a test target but nothing exists at that path, "
                + $"so that leg fails for a reason unrelated to the code under test.");
    }

    // The cancellation is the point, and `fail-fast: false` would revert it in one word
    // while every test still passed and every check stayed green — the saving would just
    // quietly stop happening. This asserts only what it can see: that the workflow asks
    // for fail-fast and nowhere turns it off. It does not model YAML scoping, so a second
    // matrix added later needs this revisited rather than trusted.
    [Fact]
    public void TheTestMatrix_KeepsFailFastOn()
    {
        var workflow = RunnableWorkflowText();

        Assert.DoesNotContain("fail-fast: false", workflow, StringComparison.Ordinal);
        Assert.Contains("fail-fast: true", workflow, StringComparison.Ordinal);
    }
}
