namespace Cluckwork.Api.IntegrationTests;

using System.Reflection;
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
    // 1. Guards inside ServingBootGuards. Every private static check method
    //    there is one, by construction of the class. Reflection rather than a
    //    list, so a fourth method added tomorrow fails this immediately.
    [Fact]
    public void EveryServingBootGuardMethod_HasACoveringRow()
    {
        var guardMethods = typeof(ServingBootGuards)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Ensure", StringComparison.Ordinal))
            .Select(m => m.Name)
            .ToArray();

        // The mapping is deliberately explicit and tiny: a method name cannot be
        // derived from a config key, and guessing would let a rename pass.
        var covered = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EnsureTrustedProxiesConfigured"] = "RateLimiting:TrustedProxies is empty",
            ["EnsureAllowedHostsPinned"] = "AllowedHosts is missing, blank, or wildcard",
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

        foreach (var token in covered.Values)
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

        var covered = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ValidateWindow guards Login/Refresh/ClientErrors permit+window; one
            // row stands for the family because they share a single code path and
            // a single message shape.
            ["ValidateWindow"] = "PermitLimit must be greater than 0",
            ["ValidateConcurrency"] = "ReportsConcurrency:QueueLimit must be 0",
            ["ParseTrustedProxies"] = "is not a valid CIDR network",
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
        var uncovered = validatedOptionTypes
            .Where(t => !ProcessRoleGuardTests.CoveredGuardTokens.Any(token =>
                token.StartsWith(
                    t.Replace("Options", string.Empty, StringComparison.Ordinal) + ":",
                    StringComparison.Ordinal)))
            .ToArray();

        Assert.True(
            uncovered.Length == 0,
            "these options types are validated at host start — so they fail a serving boot and "
            + "are skipped for one-shot verbs — but have no row in "
            + $"ProcessRoleGuardTests.ServingOnlyGuards: {string.Join(", ", uncovered)}. "
            + "Add a row so arm 1 proves the verbs survive them.");
    }
}
