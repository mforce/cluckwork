namespace Cluckwork.Api.IntegrationTests;

using System.Reflection;
using System.Text.RegularExpressions;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// #347 review round 3 — makes ProcessRoleGuardTests' table track the source.
//
// That suite derives one arm per serving-only boot guard, which is only as good
// as the table being complete. It was not enforced: deleting the #319 row
// deleted its arm and the suite went from 4 green to 3 green — silently. So the
// v2 defect (a guard proven by nothing) was re-openable by editing the test, and
// any guard added later was covered by nothing at all. "Adding a guard is a row"
// was an invariant living in a comment, which AGENTS.md calls a bug unless a
// line enforces it.
//
// These enumerate the two places a serving-only guard can be added and hold each
// against the table. Neither reads the table's own definition of what a guard is
// — that would be the tautology the registry test was corrected for.
public sealed class ServingGuardCoverageTests
{
    // 1. Guard methods anywhere in Cluckwork.Api.Hosting, not just inside
    //    ServingBootGuards.
    //
    //    Scoping this to one type was the same "list what I thought of" method
    //    that produced every other miss in this PR: #510's JWT key guard landed
    //    in CluckworkIdentityServiceCollectionExtensions, a THIRD file, and a
    //    ServingBootGuards-only walk could not see it. Walk the namespace and
    //    exclude deliberately.
    [Fact]
    public void EveryServingBootGuardMethod_HasACoveringRow()
    {
        var guardMethods = typeof(ServingBootGuards).Assembly
            .GetTypes()
            .Where(t => t.Namespace == typeof(ServingBootGuards).Namespace)
            .SelectMany(t => t.GetMethods(BindingFlags.NonPublic | BindingFlags.Static))
            .Where(m => m.Name.StartsWith("Ensure", StringComparison.Ordinal))
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // The mapping is deliberately explicit and tiny: a method name cannot be
        // derived from a config key, and guessing would let a rename pass. One
        // entry per method, listing every token that method is responsible for —
        // per VIOLATION, so a method with two branches owes two rows.
        var covered = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["EnsureTrustedProxiesConfigured"] = ["RateLimiting:TrustedProxies is empty"],
            ["EnsureAllowedHostsPinned"] = ["AllowedHosts is missing, blank, or wildcard"],
            // #510 — the role gate; its rows' one-shot arms are what prove a verb
            // still runs with no signing key at all.
            ["EnsureUsablePublicKey"] = ["Jwt:PublicKeyPem is not configured"],
            // ...and the check itself, run once per key, two branches each.
            ["EnsureUsable"] =
            [
                "Jwt:PublicKeyPem is not a usable PEM key",
                "Jwt:PrivateKeyPem is not configured",
                "Jwt:PrivateKeyPem is not a usable PEM key",
            ],
            ["EnsureSharedStateConnectionValid"] = ["SharedState:Redis:ConnectionString is set but not a valid"],
        };

        Assert.NotEmpty(guardMethods);

        var uncovered = guardMethods.Where(m => !covered.ContainsKey(m)).ToArray();
        Assert.True(
            uncovered.Length == 0,
            "ServingBootGuards has check method(s) with no row in "
            + $"ProcessRoleGuardTests.ServingOnlyGuards: {string.Join(", ", uncovered)}. "
            + "A serving-only guard with no row is proven by nothing — add a row (violate it, "
            + "satisfy the others, assert the boot dies naming it) and map it here.");

        // The reverse direction, so this mapping cannot rot either: a method
        // removed or renamed must not leave a stale entry claiming coverage.
        var stale = covered.Keys.Where(k => !guardMethods.Contains(k)).ToArray();
        Assert.True(
            stale.Length == 0,
            $"this mapping names ServingBootGuards method(s) that no longer exist: "
            + $"{string.Join(", ", stale)}. Was one renamed or removed?");

        foreach (var token in covered.Values.SelectMany(t => t))
            Assert.Contains(token, ProcessRoleGuardTests.CoveredGuardTokens);
    }

    // 2. RateLimitingOptions.Validate — the second place a serving-only guard
    //    can hide, and the one that produced two live bugs: every check in it
    //    aborted one-shot verbs at service registration. Reflected for the same
    //    reason as ServingBootGuards: a check added here tomorrow must not be
    //    silently uncovered.
    [Fact]
    public void EveryRateLimitingValidation_HasACoveringRow()
    {
        var validationMethods = typeof(RateLimitingOptions)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Validate", StringComparison.Ordinal))
            .Select(m => m.Name)
            .Concat([nameof(RateLimitingOptions.ParseTrustedProxies)])
            .ToArray();

        // One entry per CHECK, not per helper: ValidateWindow and
        // ValidateConcurrency each enforce two different things, and mapping a
        // helper to a single token left one of each pair authoritative for
        // nothing (#347 review round 3, codex).
        var covered = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["ValidateWindow"] =
            [
                "RateLimiting:Login:PermitLimit must be greater than 0",
                "RateLimiting:Login:WindowSeconds must be greater than 0",
            ],
            ["ValidateConcurrency"] =
            [
                "RateLimiting:ReportsConcurrency:PermitLimit must be greater than 0",
                "ReportsConcurrency:QueueLimit must be 0",
            ],
            ["ParseTrustedProxies"] = ["is not a valid CIDR network"],
        };

        Assert.NotEmpty(validationMethods);

        var uncovered = validationMethods.Where(m => !covered.ContainsKey(m)).ToArray();
        Assert.True(
            uncovered.Length == 0,
            "RateLimitingOptions has validation method(s) with no row in "
            + $"ProcessRoleGuardTests.ServingOnlyGuards: {string.Join(", ", uncovered)}. "
            + "Rate limiting is inbound-HTTP machinery, so its validation must not abort a "
            + "one-shot verb — add a row proving that.");

        var stale = covered.Keys.Where(k => !validationMethods.Contains(k)).ToArray();
        Assert.True(
            stale.Length == 0,
            $"this mapping names RateLimitingOptions method(s) that no longer exist: "
            + $"{string.Join(", ", stale)}. Was one renamed or removed?");

        // The half this test was missing: without it, deleting the malformed-CIDR
        // or queue-limit ROW removed both of its subprocess arms while this stayed
        // green, because the reflected methods still satisfied everything above
        // (#347 review round 3, codex).
        foreach (var token in covered.Values.SelectMany(t => t))
            Assert.Contains(token, ProcessRoleGuardTests.CoveredGuardTokens);
    }

    // 3. Guards that are serving-only by MECHANISM rather than by a role check:
    //    .ValidateOnStart() runs from Host.StartAsync, which the CLI dispatcher
    //    never reaches. These are the ones the first derivation missed entirely,
    //    so they are enumerated from the real registration rather than listed.
    [Fact]
    public void EveryValidateOnStartOption_HasACoveringRow()
    {
        var services = new ServiceCollection();
        services.AddCluckworkFeatures(new ConfigurationBuilder().Build());

        var validatedOptionTypes = services
            .Where(d => d.ServiceType.IsGenericType
                        && d.ServiceType.GetGenericTypeDefinition() == typeof(IValidateOptions<>))
            .Select(d => d.ServiceType.GetGenericArguments()[0].Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(validatedOptionTypes);

        // An options type validated at host start fails a SERVING boot and is
        // skipped for every one-shot verb, which is exactly the property the
        // table exists to pin. The row's token is the config key the validator
        // names, so match on the section prefix the options type is named for.
        //
        // Counted per BRANCH, not per type. Enumerating types only asked "is this
        // validator represented at all", and both upload-cap validators have a
        // floor branch AND a distinct ceiling branch while the table violated
        // only the floor — so deleting either ceiling check was green (#347
        // review round 5, codex). A validator's failure count is the number of
        // ValidateOptionsResult.Fail sites in its source, which is a boring
        // source scan on purpose: it cannot silently under-report the way
        // reflection over branches would have to guess.
        foreach (var type in validatedOptionTypes)
        {
            var prefix = type.Replace("Options", string.Empty, StringComparison.Ordinal) + ":";
            var rows = ProcessRoleGuardTests.CoveredGuardTokens
                .Count(token => token.StartsWith(prefix, StringComparison.Ordinal));
            var branches = FailureBranchCount(type);

            Assert.True(
                rows >= branches,
                $"{type} is validated at host start — so it fails a serving boot and is skipped "
                + $"for one-shot verbs — but has {rows} row(s) in "
                + $"ProcessRoleGuardTests.ServingOnlyGuards for {branches} failure branch(es). "
                + "Rows are per VIOLATION, not per validator: add a row violating each branch, "
                + "or a deleted check stays green.");
        }
    }

    // The validator lives beside its options type, named for it.
    private static int FailureBranchCount(string optionsTypeName)
    {
        var file = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), $"{optionsTypeName}.cs",
                SearchOption.AllDirectories)
            .SingleOrDefault();

        Assert.NotNull(file);
        return Regex.Matches(
            File.ReadAllText(file!),
            @"ValidateOptionsResult\s*\.\s*Fail\s*\(").Count;
    }

    // 4. …and the same question asked of the WHOLE source tree, because the
    //    enumeration above only executes AddCluckworkFeatures. A `.ValidateOnStart()`
    //    added tomorrow to persistence, identity, jobs or anywhere else would be a
    //    new serving-only guard that test cannot see, and `Assert.NotEmpty` would
    //    stay satisfied by the two existing validators (#347 review round 3, codex).
    //
    //    Deliberately a file-level sweep rather than a cleverer parse: it needs to
    //    answer "did a new registration site appear", and the boring version of
    //    that cannot quietly under-report.
    //
    //    The match is whitespace-tolerant and does NOT require the leading dot.
    //    The first version looked for the literal `.ValidateOnStart()`, which a
    //    line break before the parentheses, a space, or a static call through
    //    OptionsBuilderExtensions all slip past — a sweep whose whole job is
    //    "did a registration appear anywhere" must not be defeated by formatting
    //    (#347 review round 5, codex).
    [Fact]
    public void NoValidateOnStartRegistrationOutsideTheEnumeratedExtension()
    {
        var sourceRoot = Path.Combine(RepositoryRoot(), "src");
        var sites = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"ValidateOnStart\s*\("))
            .Select(f => Path.GetFileName(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["CluckworkFeatureServiceCollectionExtensions.cs"], sites);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cluckwork.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
