using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.IntegrationTests.SharedState;
using Cluckwork.Api.Modules.Access.OAuth;
using Cluckwork.Api.RateLimiting;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Modules.Farm.Accounts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Validation;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.IntegrationTests;

// #796 — an OAuth access token runs the same fail-closed chain a session JWT does. The
// probes below are the only endpoints that accept OAuth tokens, and they exist only in
// this test host; they sit in the real endpoint table, behind the real middleware.
[Collection(IntegrationCollection.Name)]
public sealed partial class OAuthFailClosedTests(CluckworkWebApplicationFactory factory)
{
    private const string ReadScope = "cw796.read";
    private const string WriteScope = "cw796.write";
    private const string RedirectUri = "https://client.example/callback";

    [Fact]
    public async Task OAuthToken_CarriesTheSessionPrincipal_ThroughTheWholeChain()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);

        var token = await IssueAsync(host, user, ReadScope);
        using var response = await Client(host, token).GetAsync(Probe.Read);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(user.Id, body.GetProperty("userId").GetGuid());
        Assert.Equal(user.AccountId, body.GetProperty("accountId").GetGuid());
        Assert.Equal([Roles.Manager], body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task DisabledUser_IsRefused_OnTheNextRequest()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var token = await IssueAsync(host, user, ReadScope);

        await factory.WithTenantScopeAsync(user.AccountId, db => db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.DisabledAt, DateTimeOffset.UtcNow)));

        await AssertRefusedAsync(host, token, "Auth.AccountDisabled");
    }

    [Fact]
    public async Task SuspendedFarm_IsRefused_OnTheNextRequest()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var token = await IssueAsync(host, user, ReadScope);

        await factory.WithTenantScopeAsync(user.AccountId, db => db.Accounts.Where(a => a.Id == user.AccountId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.IsActive, false)));

        await AssertRefusedAsync(host, token, "Auth.FarmSuspended");
    }

    // Role freshness is #364's epoch: a demotion bumps it, so the assistant's token stops
    // and the assistant must reconnect, rather than keeping Manager authority (#796 option 1).
    // The bump revokes the connection, so the token is refused before the epoch check (#1229).
    [Fact]
    public async Task RoleChange_RevokesTheToken()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var token = await IssueAsync(host, user, ReadScope, WriteScope);
        using (var before = await Client(host, token).GetAsync(Probe.AdminWrite))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using var owner = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(user.OwnerEmail));
        using var stepUp = await owner.PostAsJsonAsync("/api/v1/auth/step-up", new { password = TestHarness.Password });
        stepUp.EnsureSuccessStatusCode();
        var stepUpToken = (await stepUp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        using var demote = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{user.Id}/role")
        {
            Content = JsonContent.Create(new { role = Roles.ReadOnly }),
        };
        demote.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        demote.Headers.Add(Cluckwork.Api.Modules.Access.Auth.AuthEndpoints.StepUpHeaderName, stepUpToken);
        (await owner.SendAsync(demote)).EnsureSuccessStatusCode();

        var (authorizations, tokens) = await StillValidAsync(user);
        using var after = await Client(host, token).GetAsync(Probe.AdminWrite);
        Assert.True(authorizations == 0, $"{authorizations} authorization(s) stayed valid after a role change");
        Assert.True(tokens == 0, $"{tokens} token(s) stayed valid after a role change");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    // A user who must change their password cannot approve an assistant, so no token ever
    // carries the pending change. Any later password reset bumps the epoch (#364).
    [Fact]
    public async Task MustChangePassword_BlocksIssuance()
    {
        using var host = Host();
        var email = $"oauth-mcp-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync($"owner-{Guid.NewGuid():N}@test.local");
        await factory.SeedUserPendingPasswordChangeAsync(accountId, email, Roles.Manager);
        var jwt = await factory.LoginForAccessTokenAsync(email);
        var clientId = await RegisterClientAsync(host.Services);

        using var response = await Client(host, jwt).GetAsync(AuthorizeUri(clientId, NewVerifier(), ReadScope));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Auth.MustChangePassword", await TitleOf(response));
    }

    // The trap from #796: FlockScope.IsUnrestricted defaults to true, so a probe on it
    // passes while scoping never ran. IsResolved proves the middleware ran for this caller.
    [Fact]
    public async Task Worker_IsFlockScoped()
    {
        using var host = Host();
        var user = await SeedAsync(role: null);
        var assigned = await factory.SeedFlockAsync(user.AccountId, Guid.NewGuid());
        await factory.SeedFlockAsync(user.AccountId, Guid.NewGuid());
        await factory.WithTenantScopeAsync(user.AccountId, async db =>
        {
            db.UserRoleAssignments.Add(UserRoleAssignment.Create(
                Guid.NewGuid(), user.AccountId, user.Id, null, null, assigned));
            await db.SaveChangesAsync();
        });
        var token = await IssueAsync(host, user, ReadScope);

        using var response = await Client(host, token).GetAsync(Probe.Read);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var flocks = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("flockScope");
        Assert.True(flocks.GetProperty("isResolved").GetBoolean(), "flock scope never resolved for the OAuth caller");
        Assert.False(flocks.GetProperty("isUnrestricted").GetBoolean(), "the worker's OAuth caller is unrestricted");
        Assert.Equal([assigned], flocks.GetProperty("flockIds").EnumerateArray().Select(id => id.GetGuid()));
    }

    // Scopes only subtract: a role that allows the write does not help a token without the
    // write scope, and the scope does not help a role that forbids it.
    [Fact]
    public async Task ScopeAndRole_AreBothRequired()
    {
        using var host = Host();
        var manager = await SeedAsync(Roles.Manager);
        var readOnly = await SeedAsync(Roles.ReadOnly);

        using var noScope = await Client(host, await IssueAsync(host, manager, ReadScope)).GetAsync(Probe.AdminWrite);
        using var noRole = await Client(host, await IssueAsync(host, readOnly, WriteScope)).GetAsync(Probe.AdminWrite);

        Assert.Equal(HttpStatusCode.Forbidden, noScope.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noRole.StatusCode);
    }

    // Disconnect revokes the authorization. The token row is untouched, so only the
    // authorization-entry check can refuse it.
    [Fact]
    public async Task Disconnect_RefusesTheAccessToken_OnTheNextRequest()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var token = await IssueAsync(host, user, ReadScope);
        using (var before = await Client(host, token).GetAsync(Probe.Read))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        await DisconnectAsync(host, user.Id);

        using var after = await Client(host, token).GetAsync(Probe.Read);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    // No refresh grant exists, and a code issued before Disconnect no longer redeems.
    [Fact]
    public async Task Disconnect_LeavesNoWayToANewToken()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var clientId = await RegisterClientAsync(host.Services);
        var verifier = NewVerifier();
        var code = await AuthorizeAsync(host, user.Jwt, clientId, verifier, ReadScope);

        await DisconnectAsync(host, user.Id);

        using var redeemed = await RedeemAsync(host, clientId, code, verifier);
        Assert.Equal(HttpStatusCode.BadRequest, redeemed.StatusCode);
        Assert.Equal(Errors.InvalidGrant, await ErrorOf(redeemed));
        using var refreshed = await Client(host, bearer: null).PostAsync("/api/v1/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = GrantTypes.RefreshToken,
                ["client_id"] = clientId,
                ["refresh_token"] = "cw796-refresh",
            }));
        Assert.Equal(HttpStatusCode.BadRequest, refreshed.StatusCode);
        Assert.Equal(Errors.UnsupportedGrantType, await ErrorOf(refreshed));
    }

    // OpenIddict checks an authorization only when the token names one, so a token whose
    // row lost its authorization would otherwise outlive any Disconnect.
    [Fact]
    public async Task TokenWithoutAnAuthorization_IsRefused()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var token = await IssueAsync(host, user, ReadScope);

        await using (var scope = host.Services.CreateAsyncScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "OpenIddictTokens" SET "AuthorizationId" = NULL WHERE "Subject" = {user.Id.ToString()} AND "Type" = {TokenTypeIdentifiers.AccessToken}"""));

        using var response = await Client(host, token).GetAsync(Probe.Read);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Scheme confusion, one direction: a session JWT never authenticates where OAuth
    // tokens are accepted. The other direction is OAuthServerTests' business-endpoint wall.
    [Fact]
    public async Task SessionJwt_IsRefused_WhereOAuthTokensAreAccepted()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);

        using var response = await Client(host, user.Jwt).GetAsync(Probe.Read);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // A token in a URL reaches request logs, and outside the Authorization header it would
    // dodge the per-token rate-limit key, so only the header is read.
    [Fact]
    public async Task TokenInTheQueryString_IsIgnored()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var token = await IssueAsync(host, user, ReadScope);

        using var response = await Client(host, bearer: null)
            .GetAsync(QueryHelpers.AddQueryString(Probe.Read, "access_token", token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Keyed per token: one connection exhausting its budget leaves another untouched.
    [Fact]
    public async Task OAuthApiCalls_AreRateLimited_PerToken()
    {
        using var host = Host(limits: ("OAuthApi", 2));
        var user = await SeedAsync(Roles.Manager);
        var first = await IssueAsync(host, user, ReadScope);
        var second = await IssueAsync(host, user, ReadScope);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await Client(host, first).GetAsync(Probe.Read);
            statuses.Add(response.StatusCode);
        }
        using var other = await Client(host, second).GetAsync(Probe.Read);

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    // The per-token key is unverified text, so a forged bearer per request would never run
    // out. Each refusal spends the address's failure budget, which is checked before the
    // next token is looked up.
    [Fact]
    public async Task ForgedBearers_FromOneAddress_RunOutOfTheFailureBudget()
    {
        using var host = FrozenHost(new FakeTimeProvider());
        const int budget = RateLimitingOptions.OAuthApiFailuresPerAddress;

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i <= budget; i++)
        {
            using var response = await Client(host, $"forged-{Guid.NewGuid():N}").GetAsync(Probe.Read);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(Enumerable.Repeat(HttpStatusCode.Unauthorized, budget).Append(HttpStatusCode.TooManyRequests), statuses);
    }

    // A valid token's calls spend nothing, so it keeps working while only valid traffic flows.
    // The forged control proves the read names the key a refusal spends.
    [Fact]
    public async Task ValidTokenCalls_SpendNoFailureBudget()
    {
        using var host = FrozenHost(new FakeTimeProvider());
        var token = await IssueAsync(host, await SeedAsync(Roles.Manager), ReadScope);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var response = await Client(host, token).GetAsync(Probe.Read);
            statuses.Add(response.StatusCode);
        }
        var afterValid = await FailuresAsync(host);
        using var forged = await Client(host, "forged").GetAsync(Probe.Read);

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(0, afterValid);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(1, await FailuresAsync(host));
    }

    // Refusals reach the failure budget only when they finish, so the cap on bearers in flight
    // bounds what one address can start meanwhile. Authentication is held open here; the next
    // bearer from that address is refused, another address is not, and every held request
    // gives its place back when it finishes.
    [Fact]
    public async Task BearersInFlight_FromOneAddress_AreCapped_AndAnotherAddressIsNot()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        using var host = FrozenHost(new FakeTimeProvider()).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>();
            services.AddOpenIddict().AddValidation(validation =>
                validation.AddEventHandler<OpenIddictValidationEvents.ProcessAuthenticationContext>(handler => handler
                    .UseInlineHandler(async _ =>
                    {
                        Interlocked.Increment(ref entered);
                        await gate.Task;
                    })
                    .SetOrder(int.MinValue + 100_000)));
        }));
        const int cap = RateLimitingOptions.OAuthApiBearersInFlightPerAddress;

        HttpResponseMessage refused;
        Task<HttpResponseMessage> other;
        var held = Enumerable.Range(0, cap).Select(i => Client(host, $"forged-{i}").GetAsync(Probe.Read)).ToList();
        try
        {
            await UntilAsync(() => Volatile.Read(ref entered) == cap);
            // Without the cap this request would wait on the gate too.
            refused = await Client(host, "forged-over").GetAsync(Probe.Read).WaitAsync(TimeSpan.FromSeconds(30));
            var otherClient = Client(host, "forged-elsewhere");
            otherClient.DefaultRequestHeaders.Add("X-Test-Remote", "203.0.113.9");
            other = otherClient.GetAsync(Probe.Read);
            await UntilAsync(() => Volatile.Read(ref entered) == cap + 1);
        }
        finally
        {
            gate.TrySetResult();
        }
        var finished = await Task.WhenAll(held.Append(other));
        using var afterwards = await Client(host, "forged-after").GetAsync(Probe.Read);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.All(finished, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);
    }

    // The permit covers authentication only. Once the credential gates pass, the request gives
    // it back, so tool work held open on every slot does not refuse the next authentication.
    [Fact]
    public async Task AuthenticatedWorkInFlight_DoesNotHoldAuthenticationPermits()
    {
        var work = new HeldWork();
        using var host = FrozenHost(new FakeTimeProvider()).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(work);
            services.AddSingleton<IStartupFilter, HeldProbe>();
        }));
        var token = await IssueAsync(host, await SeedAsync(Roles.Manager), ReadScope);
        const int cap = RateLimitingOptions.OAuthApiBearersInFlightPerAddress;

        HttpResponseMessage fresh;
        var held = Enumerable.Range(0, cap).Select(_ => Client(host, token).GetAsync(HeldProbe.Path)).ToList();
        try
        {
            await UntilAsync(() => Volatile.Read(ref work.Entered) == cap);
            fresh = await Client(host, token).GetAsync(Probe.Read).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            work.Gate.TrySetResult();
        }
        var finished = await Task.WhenAll(held);

        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.All(finished, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    private sealed class HeldWork
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Entered;
    }

    // An authenticated endpoint whose work waits on the gate, standing in for a slow tool call.
    private sealed class HeldProbe(HeldWork work) : IStartupFilter
    {
        public const string Path = "/test/oauth/held";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            var endpoints = (IEndpointRouteBuilder)app.Properties["__EndpointRouteBuilder"]!;
            endpoints.MapGet(Path, async () =>
                {
                    Interlocked.Increment(ref work.Entered);
                    await work.Gate.Task;
                    return Results.Ok();
                })
                .AcceptOAuthTokens(ReadScope);
        };
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    // The budget is a window, so a spent address may try again once it rolls over.
    [Fact]
    public async Task SpentFailureBudget_RecoversWhenTheWindowRollsOver()
    {
        var clock = new FakeTimeProvider();
        using var host = FrozenHost(clock);
        var counter = host.Services.GetRequiredService<IFixedWindowCounter>();
        for (var i = 0; i < RateLimitingOptions.OAuthApiFailuresPerAddress; i++)
            await counter.IncrementAsync(FailureKey, RateLimitingOptions.OAuthApiFailuresWindow);

        using var spent = await Client(host, "forged").GetAsync(Probe.Read);
        clock.Advance(RateLimitingOptions.OAuthApiFailuresWindow);
        using var rolledOver = await Client(host, "forged").GetAsync(Probe.Read);

        Assert.Equal(HttpStatusCode.TooManyRequests, spent.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, rolledOver.StatusCode);
    }

    // A busy connection hitting its own per-token limit is not a refused credential.
    [Fact]
    public async Task PerTokenRefusals_SpendNoFailureBudget()
    {
        using var host = FrozenHost(new FakeTimeProvider(), ("OAuthApi", 2));
        var token = await IssueAsync(host, await SeedAsync(Roles.Manager), ReadScope);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await Client(host, token).GetAsync(Probe.Read);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(0, await FailuresAsync(host));
    }

    // The failure budget is read from the shared store, not this process: a store reporting
    // it spent refuses a valid token that every other check would admit.
    [Fact]
    public async Task SpentFailureBudget_InTheSharedStore_RefusesBeforeAuthentication()
    {
        using var issuing = Host();
        var token = await IssueAsync(issuing, await SeedAsync(Roles.Manager), ReadScope);
        using var host = Host().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFixedWindowCounter>();
            services.AddSingleton<IFixedWindowCounter, SpentFailureBudgetCounter>();
        }));

        using var response = await Client(host, token).GetAsync(Probe.Read);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(42), response.Headers.RetryAfter?.Delta);
    }

    private const string FailureKey = $"{RateLimitingOptions.OAuthApiFailuresKeyPrefix}:local";

    private static async Task<long> FailuresAsync(WebApplicationFactory<Program> host) =>
        (await host.Services.GetRequiredService<IFixedWindowCounter>().CountAsync(
            FailureKey, RateLimitingOptions.OAuthApiFailuresWindow)).Count;

    // The failure budget's window is a fixed minute, so a test that spends or reads it runs on
    // a counter whose clock stands still unless the test moves it.
    private WebApplicationFactory<Program> FrozenHost(FakeTimeProvider clock, params (string Policy, int PermitLimit)[] limits) =>
        Host(limits).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFixedWindowCounter>();
            services.AddSingleton<IFixedWindowCounter>(new InProcessFixedWindowCounter(clock));
        }));

    // Admits every increment; reports every failure budget spent.
    private sealed class SpentFailureBudgetCounter : IFixedWindowCounter
    {
        public long Increment(string key, TimeSpan window) => 1;

        public ValueTask<FixedWindowResult> IncrementAsync(
            string key, TimeSpan window, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new FixedWindowResult(1, window));

        public ValueTask<FixedWindowResult> CountAsync(
            string key, TimeSpan window, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new FixedWindowResult(
                key.StartsWith(RateLimitingOptions.OAuthApiFailuresKeyPrefix + ":", StringComparison.Ordinal) ? long.MaxValue : 0,
                TimeSpan.FromSeconds(42)));
    }

    [Fact]
    public async Task TokenEndpoint_IsRateLimited()
    {
        using var host = Host(limits: ("OAuthToken", 1));
        var clientId = await RegisterClientAsync(host.Services);

        using var first = await RedeemAsync(host, clientId, "cw796-code", NewVerifier());
        using var second = await RedeemAsync(host, clientId, "cw796-code", NewVerifier());

        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    // A client may hold a session bearer from elsewhere; the token endpoint ignores it
    // instead of treating the exchange as a tenant write that needs an Idempotency-Key.
    [Fact]
    public async Task TokenEndpoint_IgnoresAnAmbientSessionBearer()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var clientId = await RegisterClientAsync(host.Services);
        var verifier = NewVerifier();
        var code = await AuthorizeAsync(host, user.Jwt, clientId, verifier, ReadScope);

        using var response = await Client(host, user.Jwt).PostAsync("/api/v1/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = GrantTypes.AuthorizationCode,
                ["client_id"] = clientId,
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["code_verifier"] = verifier,
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizeEndpoint_IsRateLimited()
    {
        using var host = Host(limits: ("OAuthAuthorize", 1));
        var user = await SeedAsync(Roles.Manager);
        var clientId = await RegisterClientAsync(host.Services);

        using var first = await Client(host, user.Jwt).GetAsync(AuthorizeUri(clientId, NewVerifier(), ReadScope));
        using var second = await Client(host, user.Jwt).GetAsync(AuthorizeUri(clientId, NewVerifier(), ReadScope));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    // A POSTed authorization request matches no endpoint, so no rate-limit policy or body
    // cap; OpenIddict would still parse it and look the client up.
    [Fact]
    public async Task AuthorizationPost_IsRefusedBeforeOpenIddictReadsIt()
    {
        using var host = Host();
        var user = await SeedAsync(Roles.Manager);
        var clientId = await RegisterClientAsync(host.Services);
        using var response = await Client(host, user.Jwt).PostAsync("/api/v1/oauth/authorize",
            new FormUrlEncodedContent(AuthorizeParameters(clientId, NewVerifier(), ReadScope)!));

        Assert.Contains("Authorization requests must use GET.", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // The refusal hooks OpenIddict's authorization-request extraction only, so client
    // registration (#797), which is POST by design, is untouched by it.
    [Fact]
    public async Task Registration_StillAcceptsPost_BesideTheAuthorizeRefusal()
    {
        using var host = Host();
        var clientId = await RegisterClientAsync(host.Services);

        using var registered = await Client(host, bearer: null).PostAsJsonAsync(
            "/api/v1/oauth/register", new { redirect_uris = new[] { RedirectUri } });
        using var authorized = await Client(host, bearer: null).PostAsync("/api/v1/oauth/authorize",
            new FormUrlEncodedContent(AuthorizeParameters(clientId, NewVerifier(), ReadScope)!));

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        Assert.Contains("Authorization requests must use GET.", await authorized.Content.ReadAsStringAsync());
    }

    // The three OAuth policies count in the shared store (#543/#544), not in this process:
    // a store that refuses everything decides each response, and sees each policy's key.
    [Fact]
    public async Task OAuthLimits_AreDecidedByTheSharedCounter()
    {
        using var issuing = Host();
        var user = await SeedAsync(Roles.Manager);
        var token = await IssueAsync(issuing, user, ReadScope);
        var counter = new RefusingCounter();
        using var host = Host().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFixedWindowCounter>();
            services.AddSingleton<IFixedWindowCounter>(counter);
        }));
        var clientId = await RegisterClientAsync(host.Services);

        using var redeemed = await RedeemAsync(host, clientId, "cw796-code", NewVerifier());
        using var authorized = await Client(host, user.Jwt).GetAsync(AuthorizeUri(clientId, NewVerifier(), ReadScope));
        using var called = await Client(host, token).GetAsync(Probe.Read);

        Assert.True(redeemed.StatusCode == HttpStatusCode.TooManyRequests, "oauth-token did not ask the shared counter");
        Assert.True(authorized.StatusCode == HttpStatusCode.TooManyRequests, "oauth-authorize did not ask the shared counter");
        Assert.True(called.StatusCode == HttpStatusCode.TooManyRequests, "oauth-api did not ask the shared counter");
        var bearerKey = "oauth-api:token:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        Assert.Contains(bearerKey, counter.Keys);
    }

    // Refuses every increment. Reads nothing spent, so oauth-api's failure budget, checked
    // first, lets the per-token limit decide.
    private sealed class RefusingCounter : IFixedWindowCounter
    {
        public ConcurrentBag<string> Keys { get; } = [];

        public long Increment(string key, TimeSpan window)
        {
            Keys.Add(key);
            return long.MaxValue;
        }

        public ValueTask<FixedWindowResult> IncrementAsync(
            string key, TimeSpan window, CancellationToken cancellationToken = default)
        {
            Keys.Add(key);
            return ValueTask.FromResult(new FixedWindowResult(long.MaxValue, window));
        }

        public ValueTask<FixedWindowResult> CountAsync(
            string key, TimeSpan window, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new FixedWindowResult(0, window));
    }

    private sealed record SeededUser(Guid Id, Guid AccountId, string Jwt, string OwnerEmail);

    private async Task<SeededUser> SeedAsync(string? role)
    {
        var ownerEmail = $"oauth-owner-{Guid.NewGuid():N}@test.local";
        var email = $"oauth-user-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);
        await factory.SeedUserAsync(accountId, email, role);
        var id = await factory.WithTenantScopeAsync(accountId, db =>
            db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        return new(id, accountId, await factory.LoginForAccessTokenAsync(email), ownerEmail);
    }

    private static async Task DisconnectAsync(WebApplicationFactory<Program> host, Guid userId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        await foreach (var authorization in authorizations.FindBySubjectAsync(userId.ToString()))
            Assert.True(await authorizations.TryRevokeAsync(authorization), "the authorization was not revoked");
    }

    private static async Task AssertRefusedAsync(WebApplicationFactory<Program> host, string token, string title)
    {
        using var response = await Client(host, token).GetAsync(Probe.Read);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(title, await TitleOf(response));
    }

    private static async Task<string?> TitleOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

    private static async Task<string?> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    private static async Task<string> IssueAsync(
        WebApplicationFactory<Program> host, SeededUser user, params string[] scopes)
    {
        var clientId = await RegisterClientAsync(host.Services);
        var verifier = NewVerifier();
        var code = await AuthorizeAsync(host, user.Jwt, clientId, verifier, scopes);
        using var response = await RedeemAsync(host, clientId, code, verifier);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }

    private static async Task<string> RegisterClientAsync(IServiceProvider services)
    {
        var clientId = $"client-{Guid.NewGuid():N}";
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(
            new OpenIddictApplicationDescriptor
            {
                ClientId = clientId,
                ClientType = ClientTypes.Public,
                RedirectUris = { new Uri(RedirectUri) },
                Permissions =
                {
                    Permissions.Endpoints.Authorization,
                    Permissions.Endpoints.Token,
                    Permissions.GrantTypes.AuthorizationCode,
                    Permissions.ResponseTypes.Code,
                    Permissions.Prefixes.Scope + ReadScope,
                    Permissions.Prefixes.Scope + WriteScope,
                },
            });
        return clientId;
    }

    private static async Task<string> AuthorizeAsync(
        WebApplicationFactory<Program> host, string jwt, string clientId, string verifier, params string[] scopes)
    {
        var location = await OAuthServerTests.ApproveAsync(host, jwt, AuthorizeParameters(clientId, verifier, scopes));
        return QueryHelpers.ParseQuery(location.Query)["code"].ToString();
    }

    private static string AuthorizeUri(string clientId, string verifier, params string[] scopes) =>
        QueryHelpers.AddQueryString("/api/v1/oauth/authorize", AuthorizeParameters(clientId, verifier, scopes));

    private static Dictionary<string, string?> AuthorizeParameters(string clientId, string verifier, params string[] scopes) =>
        new()
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = ResponseTypes.Code,
            ["scope"] = string.Join(' ', scopes),
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = CodeChallengeMethods.Sha256,
        };

    private static Task<HttpResponseMessage> RedeemAsync(
        WebApplicationFactory<Program> host, string clientId, string code, string verifier) =>
        Client(host, bearer: null).PostAsync("/api/v1/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = GrantTypes.AuthorizationCode,
                ["client_id"] = clientId,
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["code_verifier"] = verifier,
            }));

    private static HttpClient Client(WebApplicationFactory<Program> host, string? bearer) =>
        OAuthServerTests.HttpsClient(host, bearer);

    private static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private WebApplicationFactory<Program> Host(params (string Policy, int PermitLimit)[] limits) =>
        factory.WithWebHostBuilder(builder =>
        {
            foreach (var (policy, permitLimit) in limits)
            {
                builder.UseSetting($"RateLimiting:{policy}:PermitLimit", permitLimit.ToString());
                // The window is clock-aligned, so two requests straddling a 60 s boundary
                // both pass; a day makes that negligible (#840's MultiInstanceRateLimitTests).
                builder.UseSetting($"RateLimiting:{policy}:WindowSeconds", "86400");
            }
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter, Probe>();
                services.Configure<OpenIddictServerOptions>(options =>
                {
                    options.Scopes.Add(ReadScope);
                    options.Scopes.Add(WriteScope);
                });
            });
        });

    // Mapped after Program.cs has built its endpoint table, into the same route builder,
    // so the probes run behind every middleware a business endpoint does.
    private sealed class Probe : IStartupFilter
    {
        public const string Read = "/test/oauth/read";
        public const string AdminWrite = "/test/oauth/admin-write";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            var endpoints = (IEndpointRouteBuilder)app.Properties["__EndpointRouteBuilder"]!;
            endpoints.MapGet(Read, (TenantContext tenant, ICurrentUser user, FlockScope flocks) => new
                {
                    accountId = tenant.AccountId,
                    userId = user.UserId,
                    roles = user.Roles,
                    flockScope = new { flocks.IsResolved, flocks.IsUnrestricted, flockIds = flocks.AssignedFlockIds },
                })
                .AcceptOAuthTokens(ReadScope)
                .RequireAuthorization();
            endpoints.MapGet(AdminWrite, () => Results.Ok())
                .AcceptOAuthTokens(WriteScope)
                .RequireAuthorization(Cluckwork.Api.AuthPolicies.AdminOnly);
        };
    }
}
