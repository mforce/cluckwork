namespace Cluckwork.Application.Tests.Documentation;

using System.Diagnostics;

// #732 — the rename's prose is not enforced by anything else, and review round 2 found
// five separate places where it said something the shipped code does not do. Each
// assertion below pins one of those corrected VALUES, so reverting any one of them is a
// red test rather than a document nobody re-reads.
public sealed class RenameAccountDocsTests
{
    private const string RenameHeading = "## Renaming a farm's code";

    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData("docs/decisions/README.md")]
    public void RenameDecision_IsLinkedFromRulesAndIndex(string relativePath)
    {
        Assert.Contains("732-farm-code-rename.md", File.ReadAllText(Path.Combine(RepoRoot(), relativePath)));
    }

    [Fact]
    public void RenameRunbook_HasExecutableExamplesAndProvisioningDrill()
    {
        var renameSection = RenameSection();

        Assert.Contains("docker run --rm --env-file", renameSection);
        Assert.DoesNotContain("immutable", renameSection, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("## Provisioning drill", Runbook().Split('\n')
            .Last(line => line.StartsWith("## ", StringComparison.Ordinal)).TrimEnd('\r'));
    }

    // Round 2 — the section was headed "Renaming the default farm's code" and excluded
    // provision-account farms outright ("Not this runbook"), which is not what the verb
    // does: it renames any farm on the deployment. The heading is the anchor other
    // sections link to, so both move together.
    [Fact]
    public void RenameRunbook_CoversEveryFarm_NotOnlyTheUpgradedDefaultOne()
    {
        var runbook = Runbook();

        Assert.Contains(RenameHeading, runbook);
        Assert.Contains("(#renaming-a-farms-code)", runbook);
        Assert.Contains("**Any farm may be renamed**", RenameSection());
        Assert.DoesNotContain("**Not this runbook:** a farm created by `provision-account`", runbook);
    }

    // Round 2 — the Verify step told the operator to expect Auth.UnknownFarmCode from
    // the old code unconditionally. There is no retired-code list, so a reused code
    // signs in perfectly well and that is not a failed rename.
    [Fact]
    public void RenameRunbook_VerifiesTheOldCodeConditionally_BecauseARetiredCodeIsReusable()
    {
        var verify = Section(RenameSection(), "### Verify");

        Assert.Contains("If no farm has reused it", verify);
        Assert.Contains("`list-accounts`", verify);
        Assert.Contains("authenticates that holder", verify);
        Assert.DoesNotContain("Confirm the old code no longer signs in", verify);
    }

    // Round 2 — three documents claimed both device caches "refresh on the next
    // explicit sign-in". The palette does, under the NEW key. The remembered code does
    // not: rememberFarmCode PREPENDS the new code and removeFarmCode (#587, the Forget
    // control) is the roster's only exit, so the old code stays offerable.
    //
    // Round 3 — each case names the EXACT corrected sentence and the EXACT false one it
    // replaced. The first version matched loose vocabulary ("prepends", "Forget",
    // "UnknownFarmCode") anywhere in the file, which any unrelated paragraph using those
    // words would satisfy: it pinned a word list, not a claim.
    [Theory]
    [InlineData(
        "docs/decisions/732-farm-code-rename.md",
        "An explicit sign-in with the new code prepends it to that device's remembered",
        "Both are cosmetic and both refresh on that sign-in.")]
    [InlineData(
        "docs/runbooks/provisioning-a-new-farm.md",
        "the OLD remembered code stays in the list until the user picks",
        "until the user next signs in explicitly")]
    [InlineData(
        "src/Cluckwork.Api/Cli/RenameAccountCliCommand.cs",
        "stays on the roster until the user picks Forget, and offering it returns",
        "and both refresh on the next explicit sign-in.")]
    public void RenameCacheClaims_SayTheOldRememberedCodeSurvivesUntilForget(
        string relativePath, string correctedClaim, string falseClaim)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

        Assert.Contains(correctedClaim, text, StringComparison.Ordinal);
        Assert.DoesNotContain(falseClaim, text, StringComparison.Ordinal);
    }

    // Round 2 — the slug stopped being immutable the moment Account.Rename shipped, and
    // three comments still said it was. The two test helpers keep their raw-SQL fixture
    // UPDATE (a setup path, not a production rename); only the claim was wrong.
    //
    // Round 3 — each case names the EXACT sentence that was wrong. Banning the bare word
    // "immutable" across a whole file bans a word rather than a claim: it would fire on a
    // future comment correctly describing an immutable value type, and it would pass a
    // file that reintroduced the same wrong claim in different words.
    [Theory]
    [InlineData(
        "src/Cluckwork.Api/Cli/ProvisionAccountCliCommand.cs",
        "The farm code is immutable during this epic.")]
    [InlineData(
        "tests/Cluckwork.Api.IntegrationTests/BootstrapAdminCommandTests.cs",
        "Slug is immutable in the domain")]
    [InlineData(
        "tests/Cluckwork.Api.IntegrationTests/RecoverAdminCommandTests.cs",
        "Slug is immutable in the domain")]
    public void SlugImmutabilityClaims_AreGone_AndNameTheRenamePathInstead(
        string relativePath, string staleClaim)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

        Assert.DoesNotContain(staleClaim, text, StringComparison.Ordinal);
        Assert.Contains("rename-account / Account.Rename", text, StringComparison.Ordinal);
    }

    // Round 2 — the enforcement list still said "two IgnoreQueryFilters reads" after
    // 04a685d folded them into one. The count is what a reader checks the allow-list
    // against, so a stale one sends them looking for a row that is not there.
    [Fact]
    public void RenameDecision_SaysOneCombinedIgnoreQueryFiltersRead()
    {
        var decision = File.ReadAllText(
            Path.Combine(RepoRoot(), "docs/decisions/732-farm-code-rename.md"));

        Assert.Contains("one combined `IgnoreQueryFilters()` read", decision);
        Assert.Contains("once directly and once through its forwarding caller", decision);
        Assert.DoesNotContain("two `IgnoreQueryFilters()` reads", decision);
    }

    // Round 2 — the committed implementation handout names code blocks and mutation rows
    // that the review rounds have since superseded. It stays as history; the note is
    // what stops the next reader executing it against this tree.
    //
    // Round 3 — the note named the range "f069469..HEAD", whose right-hand end moves with
    // every commit: read a year from now it claims a range that no longer describes the
    // review. The PR number is the fixed identifier, so the marker names that.
    [Fact]
    public void ImplementationHandout_IsMarkedHistorical_ByPrNumber_NotAMovingRange()
    {
        var handout = File.ReadAllText(
            Path.Combine(RepoRoot(), "docs/plans/732-rename-account-verb/02-implementation-runbook.md"));

        Assert.Contains(
            "**Historical implementation handout; PR #733 review-round fixes supersede named "
            + "code blocks and mutation rows. Do not execute it against the current tree.**",
            handout);
        Assert.DoesNotContain("f069469..HEAD", handout, StringComparison.Ordinal);
    }

    // #732 review round 3 — the lost-output recovery. The verb prints one line and
    // exits, so an operator whose terminal, CI log or connection ate it does not know
    // whether the rename committed. Two commands are available and only one is safe, so
    // the runbook has to say which: blindly replaying old -> new is the dangerous one,
    // because a retired code is immediately reusable and the replay would then rename
    // whoever holds it now. AccountLifecycleCommandTests pins the behaviour this prose
    // describes.
    [Fact]
    public void RenameRunbook_TellsTheOperatorHowToRecoverFromLostOutput()
    {
        var recovery = Section(RenameSection(), "### If you lost the output");

        Assert.Contains("do not blindly replay", recovery, StringComparison.Ordinal);
        Assert.Contains("`list-accounts`", recovery, StringComparison.Ordinal);
        Assert.Contains("`Account.Rename`", recovery, StringComparison.Ordinal);
        Assert.Contains("--slug <new> --new-slug <new>", recovery, StringComparison.Ordinal);
        Assert.Contains("writes no second audit row", recovery, StringComparison.Ordinal);
    }

    // #732 review round 3 — the sink's own guard. The header used to claim an unsanitized
    // stderr line "cannot be added here at all", which no comment can enforce; this reads
    // the file and makes the claim true for the shape it actually covers. Exactly one
    // Console.Error.WriteLineAsync( in the source, and it is the one inside
    // WriteErrorAsync — so a new error path either goes through the sink or reds here.
    [Fact]
    public void RenameVerb_HasExactlyOneStderrSink_AndItIsWriteErrorAsync()
    {
        const string sinkSignature = "private static Task WriteErrorAsync(string message) =>";
        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "src/Cluckwork.Api/Cli/RenameAccountCliCommand.cs"));

        var writes = Occurrences(source, "Console.Error.WriteLineAsync(");
        Assert.Single(writes);

        var sinkStart = source.IndexOf(sinkSignature, StringComparison.Ordinal);
        Assert.True(sinkStart >= 0, $"the sink '{sinkSignature}' is gone");
        var sinkEnd = source.IndexOf(';', sinkStart);
        Assert.InRange(writes[0], sinkStart, sinkEnd);
    }

    private static IReadOnlyList<int> Occurrences(string haystack, string needle)
    {
        var found = new List<int>();
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            found.Add(i);
        }

        return found;
    }

    private static string Runbook() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "docs/runbooks/provisioning-a-new-farm.md"));

    private static string RenameSection() => Section(Runbook(), RenameHeading);

    // From a heading to the next heading of the same or shallower level.
    private static string Section(string document, string heading)
    {
        var start = document.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"heading '{heading}' is missing");
        var depth = heading.TakeWhile(c => c == '#').Count();
        var body = document[(start + heading.Length)..];
        var end = Enumerable.Range(1, depth)
            .Select(level => body.IndexOf("\n" + new string('#', level) + " ", StringComparison.Ordinal))
            .Where(index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        return heading + (end >= 0 ? body[..end] : body);
    }

    private static string RepoRoot() => Git("rev-parse --show-toplevel", AppContext.BaseDirectory).Trim();

    private static string Git(string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi);
        Assert.NotNull(p);
        var output = p!.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return output;
    }
}
