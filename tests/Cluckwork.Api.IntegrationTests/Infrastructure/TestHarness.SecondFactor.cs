using System.Net;
using System.Text.Json;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// What a test keeps after enrolling a user: the key, to compute codes, and the
// recovery codes confirm returned.
internal sealed record EnrolledFactor(Guid AccountId, Guid UserId, string Key, IReadOnlyList<string> RecoveryCodes);

internal sealed record PendingLogin(string Challenge, DateTimeOffset ExpiresAt);

// #1274 decision 22: tests enrol through ConfirmEnrolmentAsync, the one writer,
// and LoginAsync completes the code step for an enrolled user.
internal static partial class TestHarness
{
    public const string SecondFactorPath = "/api/v1/auth/login/second-factor";

    public static async Task<EnrolledFactor> EnrolTwoFactorAsync(this CluckworkWebApplicationFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        var user = await FindUserAsync(db, services, email);
        services.GetRequiredService<TenantContext>().Resolve(user.AccountId);
        var credentials = services.GetRequiredService<ITwoFactorCredentials>();
        var clock = services.GetRequiredService<TimeProvider>();

        var start = await InTransactionAsync(db, ct => credentials.StartEnrolmentAsync(user.AccountId, user.Id, ct));
        var code = TotpMatcher.Compute(TotpMatcher.DecodeBase32(start.Key), TotpMatcher.StepAt(clock.GetUtcNow()));
        var outcome = await InTransactionAsync(db, ct =>
            credentials.ConfirmEnrolmentAsync(user.AccountId, user.Id, new SecondFactorCode.Totp(code), ct));

        var enrolled = Assert.IsType<ConfirmOutcome.Enrolled>(outcome);
        var factor = new EnrolledFactor(user.AccountId, user.Id, start.Key, enrolled.RecoveryCodes);
        factory.EnrolledFactors[email] = factor;
        return factor;
    }

    // Runs one write method in its own committed transaction, as a route would.
    public static Task<T> InTransactionAsync<T>(AppDbContext db, Func<CancellationToken, Task<T>> work) =>
        AmbientTransaction.RunAsync(db.Database, async (transaction, ct) =>
        {
            var result = await work(ct);
            await transaction.CommitAsync(ct);
            return result;
        });

    // The authenticator code for the lowest step in the window above the
    // user's watermark, from the host's clock. A test that signs in more than
    // about twice in 30 seconds uses a controlled clock or a recovery code.
    public static async Task<string> NextTotpCodeAsync(this CluckworkWebApplicationFactory factory, string email)
    {
        var factor = factory.EnrolledFactors[email];
        using var scope = factory.Services.CreateScope();
        var watermark = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking()
            .Where(u => u.Id == factor.UserId && u.AccountId == factor.AccountId)
            .Select(u => u.SecondFactorWatermark).SingleAsync();
        var current = TotpMatcher.StepAt(scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow());
        var step = Math.Max(current - 2, watermark + 1);
        Assert.True(step <= current + 2,
            $"every step in the window is spent for {email}; advance the test clock or use a recovery code");
        return TotpMatcher.Compute(TotpMatcher.DecodeBase32(factor.Key), step).ToString("D6");
    }

    // The password step, which must answer the pending 401, then the code step.
    // Returns the code step's raw response.
    public static async Task<HttpResponseMessage> LoginWithSecondFactorAsync(
        this CluckworkWebApplicationFactory factory, string email, string? code = null)
    {
        var pending = await factory.PasswordStepAsync(email);
        return await factory.PostSecondFactorAsync(pending.Challenge, code ?? await factory.NextTotpCodeAsync(email));
    }

    public static async Task<PendingLogin> PasswordStepAsync(
        this CluckworkWebApplicationFactory factory, string email, string password = Password)
    {
        var response = await factory.TryLoginAsync(email, password);
        return await ReadPendingLoginAsync(response);
    }

    public static async Task<PendingLogin> ReadPendingLoginAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await ReadProblemAsync(response);
        Assert.Equal(SecondFactorErrorCodes.Required, body.GetProperty("title").GetString());
        return new PendingLogin(
            body.GetProperty("challenge").GetString()!, body.GetProperty("challengeExpiresAt").GetDateTimeOffset());
    }

    public static Task<HttpResponseMessage> PostSecondFactorAsync(
        this CluckworkWebApplicationFactory factory, string challenge, string code) =>
        factory.CreateClient(Cookieless(factory)).PostAsJsonAsync(SecondFactorPath, new { challenge, code });

    // Decision 10: an enrolled user's correct password is a 401 too, so a test
    // that asserts a refused login reads the title, not the status alone.
    public static async Task<string?> LoginRefusalTitleAsync(
        this CluckworkWebApplicationFactory factory, string email, string password)
    {
        var response = await factory.TryLoginAsync(email, password);
        Assert.False(response.IsSuccessStatusCode, $"the login for {email} succeeded");
        return (await ReadProblemAsync(response)).GetProperty("title").GetString();
    }

    public static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<TokenPairDto> CompleteSecondFactorAsync(
        this CluckworkWebApplicationFactory factory, string email, HttpResponseMessage passwordStep)
    {
        var pending = await ReadPendingLoginAsync(passwordStep);
        var response = await factory.PostSecondFactorAsync(pending.Challenge, await factory.NextTotpCodeAsync(email));
        response.EnsureSuccessStatusCode();
        return await ReadSignedInTokensAsync(response);
    }

    private static async Task<TokenPairDto> ReadSignedInTokensAsync(HttpResponseMessage response)
    {
        var tokens = await ReadTokensAsync(response);
        Assert.False(string.IsNullOrEmpty(tokens.AccessToken), "a signed-in response carries an access token");
        return tokens;
    }

    private static async Task<ApplicationUser> FindUserAsync(AppDbContext db, IServiceProvider services, string email)
    {
        var normalized = services.GetRequiredService<ILookupNormalizer>().NormalizeEmail(email);
        return await db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == normalized);
    }
}
