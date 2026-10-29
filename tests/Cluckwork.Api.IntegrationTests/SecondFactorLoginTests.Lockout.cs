using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;

namespace Cluckwork.Api.IntegrationTests;

// #1274 decisions 5 and 6: wrong codes share the password's lockout, and an
// enrolled user's daily budget of wrong authenticator codes lives on the row.
public sealed partial class SecondFactorLoginTests
{
    private const string WrongPassword = "WrongPassw0rd!x9";

    // G3.5
    [Fact]
    public async Task WrongPasswordsAndWrongCodes_ShareOneLockout()
    {
        var (email, factor) = await SeedEnrolledAsync();
        await factory.TryLoginAsync(email, WrongPassword);
        await factory.TryLoginAsync(email, WrongPassword);
        var pending = await factory.PasswordStepAsync(email);
        for (var i = 0; i < 3; i++) await PostCodeAsync(pending.Challenge, WrongCode(factor));

        Assert.NotNull((await CountersAsync(factor)).LockoutEnd);
    }

    [Fact]
    public async Task AFreshCorrectPassword_DoesNotResetTheCount_AndALockedAccountRefusesTheRightCode()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var first = await factory.PasswordStepAsync(email);
        for (var i = 0; i < 4; i++) await PostCodeAsync(first.Challenge, WrongCode(factor));
        var second = await factory.PasswordStepAsync(email);
        await PostCodeAsync(second.Challenge, WrongCode(factor));

        var locked = await CountersAsync(factor);
        Assert.NotNull(locked.LockoutEnd);
        var right = await ProblemOf(await PostCodeAsync(second.Challenge, CodeAt(factor, 1)));
        Assert.Equal((HttpStatusCode.BadRequest, SecondFactorErrorCodes.Incorrect), (right.Status, right.Title));
        Assert.Equal(locked.AccessFailed, (await CountersAsync(factor)).AccessFailed);
        Assert.Equal(0, await RefreshRowsAsync(factor));
    }

    // G3.6: the user-row lock makes the charges exact under concurrency.
    [Fact]
    public async Task EightConcurrentWrongCodes_ChargeFive_AndLockOnce()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var pending = await factory.PasswordStepAsync(email);
        factory.Sink.Events.Clear();

        var answers = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => PostCodeAsync(pending.Challenge, WrongCode(factor))));

        Assert.All(answers, answer => Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode));
        var counters = await CountersAsync(factor);
        Assert.Equal(5, counters.DailyFailures);
        Assert.Equal(0, counters.AccessFailed);
        Assert.NotNull(counters.LockoutEnd);
        Assert.Single(factory.EventsFor(SecurityEvents.AccountLockedOut),
            e => e.Properties["UserId"] is ScalarValue { Value: var id } && id?.ToString() == factor.UserId.ToString());
    }

    // G3.8. Identity's lockout reads the wall clock, so the test clears it by
    // SQL after each fifth failure and moves only the 24-hour clock.
    [Fact]
    public async Task TwentyWrongCodes_SpendTheDailyBudget_UntilARecoveryCodeOrTheWindowEnds()
    {
        var (email, factor) = await SeedEnrolledAsync();
        factory.Sink.Events.Clear();
        var pending = await factory.PasswordStepAsync(email);
        var spentAt = factory.Clock.GetUtcNow();

        await SpendAsync(factor, pending.Challenge, 10);
        var signedIn = await PostCodeAsync(pending.Challenge, CodeAt(factor, 1));
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Equal(10, (await CountersAsync(factor)).DailyFailures);

        pending = await factory.PasswordStepAsync(email);
        await SpendAsync(factor, pending.Challenge, 10);
        Assert.Single(factory.EventsFor(SecurityEvents.SecondFactorBudgetSpent));

        var throttled = await PostCodeAsync(pending.Challenge, CodeAt(factor, 2));
        Assert.Equal((HttpStatusCode.TooManyRequests, new RetryLaterError(TimeSpan.Zero).Code), (throttled.StatusCode, (await ProblemOf(throttled)).Title));
        var retryAfter = TimeSpan.FromSeconds(double.Parse(throttled.Headers.GetValues("Retry-After").Single()));
        Assert.InRange(retryAfter, spentAt + TimeSpan.FromHours(24) - factory.Clock.GetUtcNow() - TimeSpan.FromSeconds(5),
            spentAt + TimeSpan.FromHours(24) - factory.Clock.GetUtcNow() + TimeSpan.FromSeconds(5));

        // A second host on the same database. It shares this process, so a count
        // held in a static would pass here; the column assertions above catch that.
        using (var secondInstance = factory.WithWebHostBuilder(_ => { }))
        {
            using var again = await secondInstance.CreateClient().PostAsJsonAsync(
                TestHarness.SecondFactorPath, new { challenge = pending.Challenge, code = CodeAt(factor, 2) });
            Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
        }

        var recovered = await PostCodeAsync(pending.Challenge, factor.RecoveryCodes[0]);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(0, (await CountersAsync(factor)).DailyFailures);

        pending = await factory.PasswordStepAsync(email);
        await SpendAsync(factor, pending.Challenge, 20);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostCodeAsync(pending.Challenge, CodeAt(factor, 2))).StatusCode);
        Assert.Equal(2, factory.EventsFor(SecurityEvents.SecondFactorBudgetSpent).Count);

        factory.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        pending = await factory.PasswordStepAsync(email);
        Assert.Equal(HttpStatusCode.OK, (await PostCodeAsync(pending.Challenge, CodeAt(factor, 0))).StatusCode);
        Assert.Equal(0, (await CountersAsync(factor)).DailyFailures);
    }

    private async Task SpendAsync(EnrolledFactor factor, string challenge, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await PostCodeAsync(challenge, WrongCode(factor))).StatusCode);
            if ((await CountersAsync(factor)).LockoutEnd is null) continue;
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users
                .Where(u => u.Id == factor.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, (DateTimeOffset?)null).SetProperty(u => u.AccessFailedCount, 0));
        }
    }
}
