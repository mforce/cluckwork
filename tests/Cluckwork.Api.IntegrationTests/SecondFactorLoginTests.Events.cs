using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Microsoft.EntityFrameworkCore;
using Serilog.Events;

namespace Cluckwork.Api.IntegrationTests;

// #1278: the code step's identity-free events and the epoch check from below.
public sealed partial class SecondFactorLoginTests
{
    [Fact]
    public async Task AWrongCode_LogsSecondFactorFailed_WithTheClientAddressOnly()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var pending = await factory.PasswordStepAsync(email);
        var code = WrongCode(factor);
        var before = factory.Sink.Events.Count;

        var response = await PostCodeAsync(pending.Challenge, code);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertIdentityFree(SecurityEvents.SecondFactorFailed, before, email, factor, pending.Challenge, code);
    }

    [Fact]
    public async Task AnExpiredChallenge_LogsSecondFactorChallengeExpired_WithTheClientAddressOnly()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var pending = await factory.PasswordStepAsync(email);
        factory.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var code = CodeAt(factor, 0);
        var before = factory.Sink.Events.Count;

        var response = await PostCodeAsync(pending.Challenge, code);

        var problem = await ProblemOf(response);
        Assert.Equal((HttpStatusCode.Unauthorized, SecondFactorErrorCodes.ChallengeExpired), (problem.Status, problem.Title));
        AssertIdentityFree(SecurityEvents.SecondFactorChallengeExpired, before, email, factor, pending.Challenge, code);
    }

    // G3.4 from below: an epoch that fell under the challenge's sf_epoch, as a
    // restored backup could leave it, refuses the challenge and charges nothing.
    [Fact]
    public async Task ACodeStep_WhoseEpochFellBelowTheChallenge_IsRefused_AndChargesNothing()
    {
        var (email, factor) = await SeedEnrolledAsync();
        await SetEpochAsync(factor, by: 1);
        var pending = await factory.PasswordStepAsync(email);
        var counters = await ChargeOnceAsync(factor, pending.Challenge);
        Assert.True(await SetEpochAsync(factor, by: -1) >= 1, "the lowered epoch must stay at or above 1, or the floor refuses it instead");

        await AssertRefusedUnchargedAsync(factor, pending.Challenge, counters);
    }

    // One wrong code first, so a refusal that reset the counters would show.
    private async Task<UserCounters> ChargeOnceAsync(EnrolledFactor factor, string challenge)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await PostCodeAsync(challenge, WrongCode(factor))).StatusCode);
        var counters = await CountersAsync(factor);
        Assert.Equal((1, 1), (counters.AccessFailed, counters.DailyFailures));
        return counters;
    }

    private async Task AssertRefusedUnchargedAsync(EnrolledFactor factor, string challenge, UserCounters counters)
    {
        var problem = await ProblemOf(await PostCodeAsync(challenge, CodeAt(factor, 0)));
        Assert.Equal((HttpStatusCode.Unauthorized, SecondFactorErrorCodes.ChallengeExpired), (problem.Status, problem.Title));
        Assert.Equal(counters, await CountersAsync(factor));
        Assert.Equal(0, await RefreshRowsAsync(factor));
    }

    private async Task<int> SetEpochAsync(EnrolledFactor factor, int by)
    {
        await factory.WithTenantScopeAsync(factor.AccountId, db => db.Users.Where(u => u.Id == factor.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.CredentialEpoch, u => u.CredentialEpoch + by)));
        return await factory.WithTenantScopeAsync(factor.AccountId, db => db.Users.AsNoTracking()
            .Where(u => u.Id == factor.UserId).Select(u => u.CredentialEpoch).SingleAsync());
    }

    // Properties the host stamps on every event in a request. Anything else the
    // event carries, a scope's values included, must be a field its
    // log-redaction-policy row names.
    private static readonly HashSet<string> HostMetadata = ["RequestId", "RequestPath", "SourceContext"];

    // The event fires once with the fields its log-redaction-policy row names,
    // the client address only. Nothing the sink received during the request,
    // in any event's message, properties or exception, names the user or a
    // credential.
    private void AssertIdentityFree(string securityEvent, int before, string email, EnrolledFactor factor, string challenge, string code)
    {
        var during = factory.Sink.Events.Skip(before).ToList();
        var emitted = Assert.Single(during, e =>
            e.Properties.TryGetValue("SecurityEvent", out var value) && value is ScalarValue { Value: string name } && name == securityEvent);
        Assert.StartsWith("`ClientIp` only.", PolicyFieldsFor(securityEvent));
        Assert.Equal(["ClientIp", "SecurityEvent"], emitted.Properties.Keys.Where(key => !HostMetadata.Contains(key)).Order());
        foreach (var logged in during)
        {
            var everything = $"{logged.RenderMessage()} {string.Join(" ", logged.Properties.Select(p => $"{p.Key}={p.Value}"))} {logged.Exception}";
            foreach (var secret in new[] { email, factor.UserId.ToString(), factor.UserId.ToString("N"), challenge, code })
                Assert.DoesNotContain(secret, everything, StringComparison.OrdinalIgnoreCase);
        }
    }

    // The last cell of the event's row in docs/security/log-redaction-policy.md.
    private static string PolicyFieldsFor(string securityEvent)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cluckwork.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var row = File.ReadLines(Path.Combine(dir.FullName, "docs", "security", "log-redaction-policy.md"))
            .Single(line => line.StartsWith($"| `{securityEvent}` |", StringComparison.Ordinal));
        return row.TrimEnd().TrimEnd('|').Split('|')[^1].Trim();
    }
}
