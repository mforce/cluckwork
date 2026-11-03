using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;

namespace Cluckwork.Api.IntegrationTests;

// #1279: SecondFactorProof's commit and transaction rules, and the step-up
// commit warning (adapted from the round-2 reviewer's probes).
public sealed partial class SecondFactorLoginTests
{
    // The record keeps the unenrolled change as today: it joins a caller's
    // transaction, and rolls back with it.
    [Fact]
    public async Task AnUnenrolledChangePassword_JoinsACallersTransaction_AndRollsBackWithIt()
    {
        var (session, _) = await PasswordSessionAsync("unenrolled");
        var before = await PasswordStateAsync(session);
        using (var scope = factory.Services.CreateScope())
        {
            scope.ResolveTenantAndActor(session.AccountId, session.UserId, session.Email);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var identity = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();
            // The caller's transaction ends without a commit.
            var changed = await AmbientTransaction.RunAsync(db.Database, (_, token) =>
                identity.ChangeOwnPasswordAsync(session.AccountId, session.UserId, TestHarness.Password, NewPassword, code: null, token));
            Assert.True(changed.IsSuccess);
        }

        Assert.Equal(before, await PasswordStateAsync(session));
        Assert.True(await PasswordSignsInAsync(session.Email, TestHarness.Password));
        Assert.Equal(HttpStatusCode.OK, await RefreshStatusAsync(session, session.Tokens.RefreshToken));
    }

    // The unit commits with CancellationToken.None, so a request cancelled
    // after the code was checked still commits once: a refused code's charge
    // stands, and an accepted code stays spent.
    [Theory]
    [InlineData("totp")]
    [InlineData("recovery")]
    [InlineData("wrong")]
    public async Task ACancelledRequestAfterTheCodeCheck_StillCommitsOnce(string kind)
    {
        var (session, code) = await PasswordSessionAsync(kind == "recovery" ? "recovery" : "totp");
        var before = await CountersAsync(session.Factor!);
        using var cancel = new CancellationTokenSource();
        using var scope = factory.Services.CreateScope();
        scope.ResolveTenantAndActor(session.AccountId, session.UserId, session.Email);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == session.UserId);
        var credentials = new CancelAfterVerify(scope.ServiceProvider.GetRequiredService<ITwoFactorCredentials>(), cancel);
        var commitsBefore = factory.CommitFault.Commits;

        var result = await SecondFactorProof.ProveAsync(db, credentials, session.AccountId, user,
            kind == "wrong" ? WrongCode(session.Factor!) : code,
            (_, _) => Task.FromResult(Result.Success(true)), commitFailed: null, cancel.Token);

        Assert.True(cancel.IsCancellationRequested, "the request was not cancelled after the code check");
        Assert.Equal(commitsBefore + 1, factory.CommitFault.Commits);
        Assert.Equal(kind != "wrong", result.IsSuccess);
        var after = await CountersAsync(session.Factor!);
        if (kind == "wrong")
            Assert.Equal((before.AccessFailed + 1, before.DailyFailures + 1), (after.AccessFailed, after.DailyFailures));
        else if (kind == "recovery")
            Assert.Equal(9, await RecoveryCodesLeftAsync(session.Factor!));
        else
            Assert.True(after.Watermark > before.Watermark, "the accepted code was not spent");
    }

    // A refusal from the route's own work after an accepted code rolls the
    // unit back, so the code is not spent and can be used again.
    [Theory]
    [InlineData("totp")]
    [InlineData("recovery")]
    public async Task ARefusalFromTheAcceptedWork_RollsTheCodeBack(string kind)
    {
        var (session, code) = await PasswordSessionAsync(kind);
        var before = await CountersAsync(session.Factor!);
        var commitsBefore = factory.CommitFault.Commits;
        using (var scope = factory.Services.CreateScope())
        {
            scope.ResolveTenantAndActor(session.AccountId, session.UserId, session.Email);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == session.UserId);
            var result = await SecondFactorProof.ProveAsync(db,
                scope.ServiceProvider.GetRequiredService<ITwoFactorCredentials>(), session.AccountId, user, code,
                (_, _) => Task.FromResult(Result.Failure<bool>(Error.Validation("Test.Refused", "Refused by the route."))),
                commitFailed: null, CancellationToken.None);
            Assert.True(result.IsFailure);
        }

        Assert.Equal(commitsBefore, factory.CommitFault.Commits);
        Assert.Equal(before, await CountersAsync(session.Factor!));
        if (kind == "recovery")
            Assert.Equal(10, await RecoveryCodesLeftAsync(session.Factor!));
        Assert.Equal(HttpStatusCode.OK, (await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, code)).StatusCode);
    }

    // The swallowed commit failure is a warning with the exception and no
    // request data: neither the code nor the password reaches the log.
    [Fact]
    public async Task AStepUpCommitFailure_LogsAWarningWithoutCredentials()
    {
        var (_, factor, client) = await SignedInEnrolledAsync();
        var code = factor.RecoveryCodes[0];
        factory.Sink.Events.Clear();
        factory.CommitFault.ArmOnce();

        var response = await StepUpAsync(client, TestHarness.Password, code);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var warning = Assert.Single(factory.Sink.Events, e =>
            e.Level == LogEventLevel.Warning
            && e.Properties.TryGetValue("SourceContext", out var source)
            && source.ToString().Contains(nameof(StepUpGrantService), StringComparison.Ordinal));
        Assert.NotNull(warning.Exception);
        var logged = warning.RenderMessage() + warning.Exception + string.Join(';', warning.Properties);
        Assert.DoesNotContain(code, logged, StringComparison.Ordinal);
        Assert.DoesNotContain(TestHarness.Password, logged, StringComparison.Ordinal);
    }

    // Cancels the request right after the code check, before the commit.
    private sealed class CancelAfterVerify(ITwoFactorCredentials inner, CancellationTokenSource cancel) : ITwoFactorCredentials
    {
        public async Task<FactorOutcome> VerifyAsync(
            Guid accountId, Guid userId, SecondFactorCode code, ProofBinding binding, CancellationToken ct)
        {
            var outcome = await inner.VerifyAsync(accountId, userId, code, binding, ct);
            await cancel.CancelAsync();
            return outcome;
        }

        public Task<EnrolmentStart> StartEnrolmentAsync(Guid accountId, Guid userId, CancellationToken ct) =>
            inner.StartEnrolmentAsync(accountId, userId, ct);

        public Task<ConfirmOutcome> ConfirmEnrolmentAsync(
            Guid accountId, Guid userId, SecondFactorCode.Totp code, CancellationToken ct) =>
            inner.ConfirmEnrolmentAsync(accountId, userId, code, ct);
    }
}
