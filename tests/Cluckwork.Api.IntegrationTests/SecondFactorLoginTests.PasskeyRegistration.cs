using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Jobs;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #1284: registering a passkey (record 1282 decisions 3, 10, 15, 16), G7.3 to G7.5.
public sealed partial class SecondFactorLoginTests
{
    private const string PasskeysPath = "/api/v1/auth/passkeys";

    private sealed record Ceremony(Guid Id, JsonElement Options);

    private static async Task<Ceremony> StartPasskeyAsync(HttpClient client)
    {
        var response = await client.PostAsync(PasskeysPath + "/options", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Ceremony(body.GetProperty("ceremony").GetGuid(), body.GetProperty("options").Clone());
    }

    // No Idempotency-Key: the prefix is on ResponseNotCacheable.
    private static Task<HttpResponseMessage> FinishPasskeyAsync(
        HttpClient client, string? grant, Guid ceremony, string credentialJson, string name = "Phone")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, PasskeysPath)
        {
            Content = JsonContent.Create(new { ceremony, credential = JsonDocument.Parse(credentialJson).RootElement, name }),
        };
        if (grant is not null) request.Headers.Add(AuthEndpoints.StepUpHeaderName, grant);
        return client.SendAsync(request);
    }

    // The whole flow for a user who steps up with the password, and a code
    // when they have the app.
    private async Task<HttpResponseMessage> RegisterPasskeyAsync(
        HttpClient client, SoftwareAuthenticator authenticator, string? code = null)
    {
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password, code));
        var ceremony = await StartPasskeyAsync(client);
        return await FinishPasskeyAsync(client, grant, ceremony.Id, authenticator.Attest(ceremony.Options));
    }

    private Task<(int Count, int Rows)> PasskeyStateAsync(Guid accountId, Guid userId) =>
        factory.PasskeyStateAsync(accountId, userId);

    // The ceremony table has no tenant, so a plain scope reads it.
    private async Task<T> CeremoniesAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<bool> CeremonyExistsAsync(Guid ceremony) =>
        CeremoniesAsync(db => db.PasskeyCeremonies.AnyAsync(c => c.Id == ceremony));

    private Task SeedPasskeysAsync(Guid accountId, Guid userId, int count) =>
        factory.SeedPasskeysAsync(accountId, userId, count);

    // G7.3, G7.5: a first passkey with no app ends every other session and
    // connected app, and this device gets a fresh pair.
    [Fact]
    public async Task AFirstPasskey_WithNoApp_EndsEveryOtherSession_AndKeepsThisDevice()
    {
        var (_, accountId, userId, client, acting) = await SignedInPlainAsync(asOwner: false);
        var email = await factory.WithTenantScopeAsync(accountId, db => db.Users.Where(u => u.Id == userId).Select(u => u.Email!).SingleAsync());
        var other = await factory.LoginAsync(email);
        var app = await ConnectedAppToken.IssueAsync(factory, other.AccessToken);
        var records = await IdempotencyRecordsAsync(accountId);
        var authenticator = new SoftwareAuthenticator();

        var response = await RegisterPasskeyAsync(client, authenticator);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(authenticator.Id, body.GetProperty("passkey").GetProperty("id").GetString());
        Assert.True(body.GetProperty("passkey").GetProperty("synced").GetBoolean());
        var fresh = body.GetProperty("accessToken").GetString()!;
        Assert.Equal((1, 1), await PasskeyStateAsync(accountId, userId));
        Assert.Equal(records, await IdempotencyRecordsAsync(accountId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(other.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(acting.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(accountId, other.RefreshToken)).StatusCode);
        await AssertEverySessionAndAppEndedAsync(accountId, userId, freshRefreshRows: 1);
        Assert.Equal(HttpStatusCode.Unauthorized, await AppProbeAsync(app));
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(fresh)).StatusCode);
        var audit = Assert.Single(await AuditRowsAsync(accountId, userId, AuditActions.UserPasskeyRegistered));
        Assert.Contains("\"firstFactor\":true", audit.Details);
    }

    // G7.5: a second passkey, and a first beside the app, end nothing.
    [Fact]
    public async Task ASecondPasskey_AndAFirstBesideTheApp_EndNothing()
    {
        var (_, accountId, userId, client, acting) = await SignedInPlainAsync(asOwner: false);
        await SeedPasskeysAsync(accountId, userId, 1);
        var (email, factor) = await SeedEnrolledAsync(asOwner: false);
        var appUser = factory.CreateAuthedClient((await factory.LoginAsync(email)).AccessToken);
        var appOther = await factory.LoginAsync(email);

        var second = await RegisterPasskeyAsync(client, new SoftwareAuthenticator());
        var besideApp = await RegisterPasskeyAsync(appUser, new SoftwareAuthenticator(), factor.RecoveryCodes[0]);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.Created, besideApp.StatusCode);
        Assert.False((await second.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("accessToken", out _));
        Assert.Equal((2, 2), await PasskeyStateAsync(accountId, userId));
        Assert.Equal((1, 1), await PasskeyStateAsync(factor.AccountId, factor.UserId));
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(acting.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(appOther.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(factor.AccountId, appOther.RefreshToken)).StatusCode);
    }

    // G7.3: the refusals after the ceremony is consumed answer 400 with the
    // one body, register nothing, and leave the ceremony gone (G7.4).
    private async Task AssertRefusedPasskeyAttestationAsync(Func<JsonElement, string> attest)
    {
        var (_, accountId, userId, client, _) = await SignedInPlainAsync(asOwner: false);
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password));
        var ceremony = await StartPasskeyAsync(client);
        var failed = factory.EventsFor(SecurityEvents.PasskeyFailed).Count;

        var response = await FinishPasskeyAsync(client, grant, ceremony.Id, attest(ceremony.Options));

        Assert.Equal((HttpStatusCode.BadRequest, "Auth.PasskeyRefused"), await TitleOf(response));
        Assert.Equal((0, 0), await PasskeyStateAsync(accountId, userId));
        Assert.False(await CeremonyExistsAsync(ceremony.Id));
        Assert.Equal(failed + 1, factory.EventsFor(SecurityEvents.PasskeyFailed).Count);
    }

    [Fact]
    public Task APasskeyWithoutUserVerification_IsRefused() =>
        AssertRefusedPasskeyAttestationAsync(options => new SoftwareAuthenticator().Attest(options, uv: false));

    [Fact]
    public Task APasskeyFromAnOriginOutsideTheList_IsRefused() =>
        AssertRefusedPasskeyAttestationAsync(options => new SoftwareAuthenticator().Attest(options, origin: "https://evil.example"));

    // G7.3: a used, an expired and another user's ceremony are refused.
    [Fact]
    public async Task AUsedExpiredForeignOrOtherPurposePasskeyCeremony_IsRefused()
    {
        var (_, accountId, userId, client, _) = await SignedInPlainAsync(asOwner: false);
        var (_, otherAccount, otherUser, otherClient, _) = await SignedInPlainAsync(asOwner: false);
        await SeedPasskeysAsync(accountId, userId, 1);

        // Used: the first finish registers, the same ceremony again is refused.
        var used = await StartPasskeyAsync(client);
        var authenticator = new SoftwareAuthenticator();
        Assert.Equal(HttpStatusCode.Created, (await FinishPasskeyAsync(client,
            await GrantOf(await StepUpAsync(client, TestHarness.Password)), used.Id, authenticator.Attest(used.Options))).StatusCode);
        var again = await FinishPasskeyAsync(client, await GrantOf(await StepUpAsync(client, TestHarness.Password)),
            used.Id, new SoftwareAuthenticator().Attest(used.Options));

        // Another user's: bob finishes alice's ceremony.
        var alices = await StartPasskeyAsync(client);
        var foreign = await FinishPasskeyAsync(otherClient, await GrantOf(await StepUpAsync(otherClient, TestHarness.Password)),
            alices.Id, new SoftwareAuthenticator().Attest(alices.Options));

        // Another purpose: a registration state stored as a step-up ceremony
        // of this user, so only the purpose check can refuse it.
        var source = await StartPasskeyAsync(client);
        var otherPurpose = Guid.NewGuid();
        await CeremoniesAsync(async db =>
        {
            var row = await db.PasskeyCeremonies.AsNoTracking().SingleAsync(c => c.Id == source.Id);
            db.PasskeyCeremonies.Add(new PasskeyCeremony
            {
                Id = otherPurpose, Purpose = CeremonyPurpose.StepUp, UserId = userId, State = row.State, ExpiresAt = row.ExpiresAt,
            });
            return await db.SaveChangesAsync();
        });
        var wrongPurpose = await FinishPasskeyAsync(client, await GrantOf(await StepUpAsync(client, TestHarness.Password)),
            otherPurpose, new SoftwareAuthenticator().Attest(source.Options));

        // Expired: five minutes and a second later.
        var expired = await StartPasskeyAsync(client);
        factory.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password));
        var late = await FinishPasskeyAsync(client, grant, expired.Id, new SoftwareAuthenticator().Attest(expired.Options));

        Assert.Equal((HttpStatusCode.BadRequest, "Auth.PasskeyRefused"), await TitleOf(again));
        Assert.Equal((HttpStatusCode.BadRequest, "Auth.PasskeyRefused"), await TitleOf(foreign));
        Assert.Equal((HttpStatusCode.BadRequest, "Auth.PasskeyRefused"), await TitleOf(late));
        Assert.Equal((HttpStatusCode.BadRequest, "Auth.PasskeyRefused"), await TitleOf(wrongPurpose));
        Assert.Equal((2, 2), await PasskeyStateAsync(accountId, userId));
        Assert.Equal((0, 0), await PasskeyStateAsync(otherAccount, otherUser));
        Assert.False(await CeremonyExistsAsync(alices.Id));
        Assert.False(await CeremonyExistsAsync(expired.Id));
    }

    // G7.3: a credential already registered, to this user or another, is refused.
    [Fact]
    public async Task APasskeyAlreadyRegistered_IsRefused()
    {
        var (_, accountId, userId, client, _) = await SignedInPlainAsync(asOwner: false);
        await SeedPasskeysAsync(accountId, userId, 1);
        var authenticator = new SoftwareAuthenticator();
        Assert.Equal(HttpStatusCode.Created, (await RegisterPasskeyAsync(client, authenticator)).StatusCode);

        var twice = await RegisterPasskeyAsync(client, authenticator);

        Assert.Equal((HttpStatusCode.BadRequest, "Auth.PasskeyRefused"), await TitleOf(twice));
        Assert.Equal((2, 2), await PasskeyStateAsync(accountId, userId));
    }

    // G7.3: a sixth passkey is 409 and the count stays 5.
    [Fact]
    public async Task ASixthPasskey_Is409_AndTheCountStaysFive()
    {
        var (_, accountId, userId, client, _) = await SignedInPlainAsync(asOwner: false);
        await SeedPasskeysAsync(accountId, userId, 5);

        var response = await RegisterPasskeyAsync(client, new SoftwareAuthenticator());

        Assert.Equal((HttpStatusCode.Conflict, "Auth.PasskeyLimitReached"), await TitleOf(response));
        Assert.Equal((5, 5), await PasskeyStateAsync(accountId, userId));
    }

    // G7.3: two concurrent sixth registrations leave 5. The first pauses
    // before its insert, after reading the count under the user-row lock; the
    // second then runs. With the lock it waits, so it reads 5 and is refused.
    [Fact]
    public async Task TwoConcurrentSixthPasskeys_LeaveFive()
    {
        var (_, accountId, userId, client, session) = await SignedInPlainAsync(asOwner: false);
        await SeedPasskeysAsync(accountId, userId, 4);
        var other = factory.CreateAuthedClient(session.AccessToken);
        var (grantA, grantB) = (await GrantOf(await StepUpAsync(client, TestHarness.Password)),
            await GrantOf(await StepUpAsync(other, TestHarness.Password)));
        var (a, b) = (await StartPasskeyAsync(client), await StartPasskeyAsync(other));
        var reachedInsert = new TaskCompletionSource();
        var resumeFirst = new TaskCompletionSource();
        factory.BeforeCommand.Arm("INSERT INTO \"AspNetUserPasskeys\"", async () =>
        {
            reachedInsert.SetResult();
            await resumeFirst.Task;
        });

        var first = FinishPasskeyAsync(client, grantA, a.Id, new SoftwareAuthenticator().Attest(a.Options));
        await reachedInsert.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = FinishPasskeyAsync(other, grantB, b.Id, new SoftwareAuthenticator().Attest(b.Options));
        // With the lock the second waits on the first, so this times out.
        await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(2)));
        resumeFirst.SetResult();
        var answers = new[] { await first, await second };

        Assert.Equal(
            [HttpStatusCode.Created, HttpStatusCode.Conflict],
            answers.Select(r => r.StatusCode).Order());
        Assert.Equal((5, 5), await PasskeyStateAsync(accountId, userId));
    }

    // G7.4: two concurrent finishes of one ceremony register once. Two
    // authenticators answer the same challenge, so only the ceremony's single
    // use can refuse the second.
    [Fact]
    public async Task TwoConcurrentFinishesOfOnePasskeyCeremony_RegisterOnce()
    {
        var (_, accountId, userId, client, session) = await SignedInPlainAsync(asOwner: false);
        await SeedPasskeysAsync(accountId, userId, 1);
        var other = factory.CreateAuthedClient(session.AccessToken);
        var (grantA, grantB) = (await GrantOf(await StepUpAsync(client, TestHarness.Password)),
            await GrantOf(await StepUpAsync(other, TestHarness.Password)));
        var ceremony = await StartPasskeyAsync(client);
        var reachedConsume = new TaskCompletionSource();
        var resumeFirst = new TaskCompletionSource();
        factory.BeforeCommand.Arm("DELETE FROM passkey_ceremonies", async () =>
        {
            reachedConsume.SetResult();
            await resumeFirst.Task;
        });

        var first = FinishPasskeyAsync(client, grantA, ceremony.Id, new SoftwareAuthenticator().Attest(ceremony.Options));
        await reachedConsume.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await FinishPasskeyAsync(other, grantB, ceremony.Id, new SoftwareAuthenticator().Attest(ceremony.Options));
        resumeFirst.SetResult();

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal((HttpStatusCode.BadRequest, "Auth.PasskeyRefused"), await TitleOf(await first));
        Assert.Equal((2, 2), await PasskeyStateAsync(accountId, userId));
    }

    // G7.4: a superseded session's finish is refused and still spends the
    // ceremony. The password changes while the request waits for the lock.
    [Fact]
    public async Task APasskeyFinishWhoseSessionWasSuperseded_IsRefused_AndSpendsTheCeremony()
    {
        var (_, accountId, userId, client, _) = await SignedInPlainAsync(asOwner: false);
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password));
        var ceremony = await StartPasskeyAsync(client);
        factory.BeforeCommand.Arm("FOR NO KEY UPDATE", async () =>
        {
            using var scope = factory.Services.CreateScope().ResolveTenantAndActor(accountId, userId);
            var done = await scope.ServiceProvider.GetRequiredService<Cluckwork.Application.Modules.Access.Users.IIdentityProvider>()
                .SetUserPasswordAsync(accountId, userId, NewPassword);
            Assert.True(done.IsSuccess);
        });

        var response = await FinishPasskeyAsync(client, grant, ceremony.Id, new SoftwareAuthenticator().Attest(ceremony.Options));

        Assert.True(factory.BeforeCommand.Fired, "the concurrent password change never ran");
        Assert.Equal((HttpStatusCode.Unauthorized, "Auth.CredentialsSuperseded"), await TitleOf(response));
        Assert.Equal((0, 0), await PasskeyStateAsync(accountId, userId));
        Assert.False(await CeremonyExistsAsync(ceremony.Id));
    }

    // G7.4: the sweep deletes expired ceremonies only.
    [Fact]
    public async Task ThePasskeyCeremonySweep_DeletesOnlyExpiredRows()
    {
        var now = factory.Clock.GetUtcNow();
        PasskeyCeremony Row(TimeSpan fromNow) => new()
        {
            Id = Guid.NewGuid(), Purpose = CeremonyPurpose.Registration, State = "state", ExpiresAt = now + fromNow,
        };
        var (expired, live) = (Row(TimeSpan.FromSeconds(-1)), Row(TimeSpan.FromMinutes(1)));
        await CeremoniesAsync(db =>
        {
            db.PasskeyCeremonies.AddRange(expired, live);
            return db.SaveChangesAsync();
        });

        await factory.Services.GetRequiredService<PasskeyCeremonyPurgeSweep>().RunAsync(CancellationToken.None);

        Assert.False(await CeremonyExistsAsync(expired.Id));
        Assert.True(await CeremonyExistsAsync(live.Id));
    }

    // G7.4: every route under /api/v1/auth/passkeys answers without an
    // Idempotency-Key and stores no idempotency record.
    [Fact]
    public async Task EveryPasskeyRoute_NeedsNoIdempotencyKey_AndStoresNoRecord()
    {
        var (_, accountId, _, client, _) = await SignedInPlainAsync(asOwner: false);
        var routes = factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/auth/passkeys", StringComparison.Ordinal) == true)
            .SelectMany(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods
                .Select(method => (Method: method, Path: e.RoutePattern.RawText!.Replace("{id}", "AQID", StringComparison.Ordinal))))
            .ToList();
        Assert.True(routes.Count >= 5, $"the walk found {routes.Count} passkey routes");
        var records = await IdempotencyRecordsAsync(accountId);

        foreach (var (method, path) in routes)
        {
            var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = method is "GET" ? null : JsonContent.Create(new { name = "Laptop" }),
            });
            var (status, title) = await TitleOf(response);
            Assert.False(title?.StartsWith("Idempotency-Key", StringComparison.Ordinal) == true
                || status == HttpStatusCode.InternalServerError,
                $"{method} {path} answered {(int)status} {title}");
        }

        Assert.Equal(records, await IdempotencyRecordsAsync(accountId));
    }
}
