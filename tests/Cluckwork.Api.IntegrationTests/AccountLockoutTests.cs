namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #128 — the configured account lockout must actually fire: repeated failed
// logins lock the account (rejecting even the correct password), a success
// resets the counter, and lockout is per-account. The shared factory disables
// the #143 per-IP rate limiter, so these multi-attempt loops exercise lockout
// in isolation.
[Collection(IntegrationCollection.Name)]
public sealed class AccountLockoutTests(CluckworkWebApplicationFactory factory)
{
    private const int MaxAttempts = 5; // opts.Lockout.MaxFailedAccessAttempts
    private const string WrongPassword = "WrongPassw0rd!x";

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });

    private async Task<string> SeedUserAsync()
    {
        var email = $"lock-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        return email;
    }

    [Fact]
    public async Task Account_locks_after_max_failed_attempts_and_then_rejects_the_correct_password()
    {
        var email = await SeedUserAsync();
        var client = factory.CreateClient();

        for (var i = 0; i < MaxAttempts; i++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await PostLoginAsync(client, email, WrongPassword)).StatusCode);

        // Locked now: the CORRECT password is refused with the same generic 401.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await PostLoginAsync(client, email, TestHarness.Password)).StatusCode);
    }

    [Fact]
    public async Task Successful_login_resets_the_failure_count()
    {
        var email = await SeedUserAsync();
        var client = factory.CreateClient();

        // One below the threshold — not locked yet.
        for (var i = 0; i < MaxAttempts - 1; i++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await PostLoginAsync(client, email, WrongPassword)).StatusCode);

        // Correct password succeeds and resets the counter to zero.
        Assert.Equal(HttpStatusCode.OK,
            (await PostLoginAsync(client, email, TestHarness.Password)).StatusCode);

        // Without the reset, 4 + 4 failures would cross the threshold and lock the
        // account; with it the count is only 4, so the correct password still works.
        for (var i = 0; i < MaxAttempts - 1; i++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await PostLoginAsync(client, email, WrongPassword)).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await PostLoginAsync(client, email, TestHarness.Password)).StatusCode);
    }

    [Fact]
    public async Task Lockout_is_per_account_not_global()
    {
        var victim = await SeedUserAsync();
        var bystander = await SeedUserAsync();
        var client = factory.CreateClient();

        for (var i = 0; i < MaxAttempts; i++)
            await PostLoginAsync(client, victim, WrongPassword);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await PostLoginAsync(client, victim, TestHarness.Password)).StatusCode);

        // A different account is untouched — lockout counts failures per user.
        Assert.Equal(HttpStatusCode.OK,
            (await PostLoginAsync(client, bystander, TestHarness.Password)).StatusCode);
    }
}
