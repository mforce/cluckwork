using System.Xml.Linq;

namespace Cluckwork.Api.IntegrationTests;

// #775 — the four test projects are legs of ci.yml's `tests` matrix. One
// `dotnet test Cluckwork.slnx` could not leave a project unrun; a matrix CAN, and it fails
// SILENTLY — a new project simply never executes while every check stays green.
//
// This is a TRIPWIRE on the solution's inventory, and deliberately nothing more. It does
// not read ci.yml at all. The first version of this guard did, by scraping the workflow
// text for project paths, and an adversarial pass showed it was wrong in both directions:
// three separate edits left it GREEN while a leg stopped running — an `if:` on the Test
// step, deleting a leg while an inline comment still named it, and `continue-on-error` on
// the job — and omitting `fail-fast` turned it RED although GitHub defaults that to true.
// Treating "mentioned in the file" as "scheduled by GitHub" is not a boundary a regex can
// hold, and AGENTS.md is explicit that a wrong guard is worse than none because it reads
// as safety.
//
// So this asserts only what it can actually establish: the set of test projects in the
// solution is the set someone last reconciled with CI. Adding or removing one fails here,
// which forces the author to go and add or remove the matching leg. It makes NO claim
// about whether the workflow then runs them — see the decision record for what is
// consequently unguarded.
public sealed class SolutionTestProjectSplitTests
{
    // Reconciled by hand against ci.yml's `tests` matrix, and that is the point: the list
    // is a checkpoint, not a discovery mechanism. A literal list would be the wrong tool
    // for finding the projects (AGENTS.md: walk everything, exclude deliberately) and is
    // the right tool for noticing that the inventory moved.
    private static readonly string[] ReconciledWithCiMatrix =
    [
        "tests/Cluckwork.Api.IntegrationTests",
        "tests/Cluckwork.AppHost.Tests",
        "tests/Cluckwork.Application.Tests",
        "tests/Cluckwork.Domain.Tests",
    ];

    private static readonly string RepositoryRoot = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cluckwork.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the Cluckwork repository root.");
    }

    private static IReadOnlyList<string> SolutionTestProjectDirectories() =>
        [.. XDocument.Load(Path.Combine(RepositoryRoot, "Cluckwork.slnx"))
            .Descendants("Project")
            .Select(project => (project.Attribute("Path")?.Value
                ?? throw new InvalidDataException("A solution project has no Path attribute."))
                .Replace('\\', '/'))
            .Where(path => path.StartsWith("tests/", StringComparison.Ordinal))
            .Select(path => Path.GetDirectoryName(path)!.Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    [Fact]
    public void TheSolutionsTestProjects_AreTheSetLastReconciledWithTheCiMatrix()
    {
        var inSolution = SolutionTestProjectDirectories();

        // A walk that found nothing would compare two empty sets and prove nothing.
        Assert.NotEmpty(inSolution);

        Assert.Equal(
            ReconciledWithCiMatrix.Order(StringComparer.Ordinal),
            inSolution);
    }
}
