using System.Net;
using Cluckwork.Api.Configuration;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.IntegrationTests.Mcp;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Api.Modules.Access.OAuth;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog.Events;

namespace Cluckwork.Api.IntegrationTests;

// #1281: every Owner must use two-factor once the grace period ends
// (#1274 decision 18, G6.1, G6.2, G6.7). A test past the deadline sets
// SecondFactorRequiredSince in the past directly; a test of the first request
// nulls it and checks it null first.
[Collection(SecondFactorCollection.Name)]
public sealed class OwnerTwoFactorGateTests(SecondFactorFactory factory) : IDisposable
{
    public void Dispose() => factory.Clock.Reset();

    private static readonly TimeSpan PastTheDeadline = TimeSpan.FromDays(OwnerGraceOptions.DefaultDays + 1);

    // The routes the gate lets an unenrolled Owner reach past the deadline,
    // as the walk names them. Logout is anonymous, so the walk never sends it.
    private static readonly string[] OpenRoutes =
    [
        "POST /api/v1/auth/change-password",
        "POST /api/v1/auth/step-up",
        "GET /api/v1/auth/two-factor",
        "POST /api/v1/auth/two-factor/start",
        "POST /api/v1/auth/two-factor/confirm",
    ];

    private sealed record Seeded(string Email, Guid AccountId, Guid UserId);

    private async Task<Seeded> SeedAsync(string? role = Roles.Owner)
    {
        var ownerEmail = $"gate-owner-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);
        var email = ownerEmail;
        if (role != Roles.Owner)
        {
            email = $"gate-user-{Guid.NewGuid():N}@test.local";
            await factory.SeedUserAsync(accountId, email, role);
        }
        var userId = await factory.WithTenantScopeAsync(accountId, db =>
            db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        return new(email, accountId, userId);
    }

    private Task SetStartAsync(Seeded user, DateTimeOffset? start) =>
        factory.WithTenantScopeAsync(user.AccountId, db => db.Users.Where(u => u.Id == user.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecondFactorRequiredSince, start)));

    private Task<DateTimeOffset?> StartAsync(Seeded user) =>
        factory.WithTenantScopeAsync(user.AccountId, db => db.Users.AsNoTracking()
            .Where(u => u.Id == user.UserId).Select(u => u.SecondFactorRequiredSince).SingleAsync());

    private async Task NullStartAsync(Seeded user)
    {
        await SetStartAsync(user, null);
        Assert.Null(await StartAsync(user));
    }

    private Task<HttpResponseMessage> GetAsync(string token, string path = "/api/v1/users") =>
        factory.CreateAuthedClient(token).GetAsync(path);

    private static async Task<string?> TitleAsync(HttpResponseMessage response) =>
        response.Content.Headers.ContentLength == 0 ? null
            : (await TestHarness.ReadProblemAsync(response)).TryGetProperty("title", out var title) ? title.GetString() : null;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOwnersFirstRequest_SetsTheStartOnce_FromTheHostClock(bool enrolled)
    {
        var owner = await SeedAsync();
        if (enrolled) await factory.EnrolTwoFactorAsync(owner.Email);
        var token = await factory.LoginForAccessTokenAsync(owner.Email);
        await NullStartAsync(owner);
        factory.Clock.Freeze();
        var firstAt = factory.Clock.GetUtcNow();

        using var first = await GetAsync(token);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        using var second = await GetAsync(token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstAt, await StartAsync(owner));
    }

    [Fact]
    public async Task TwoConcurrentFirstRequests_BothPass_AndLeaveOneStart()
    {
        var owner = await SeedAsync();
        var token = await factory.LoginForAccessTokenAsync(owner.Email);
        await NullStartAsync(owner);

        var responses = await Task.WhenAll(GetAsync(token), GetAsync(token));
        var start = await StartAsync(owner);
        using var later = await GetAsync(token);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.NotNull(start);
        Assert.Equal(start, await StartAsync(owner));
    }

    [Theory]
    [InlineData(Roles.Manager)]
    [InlineData(Roles.ReadOnly)]
    [InlineData(null)]
    public async Task ANonOwnersRequest_WritesNoStart(string? role)
    {
        var user = await SeedAsync(role);
        var token = await factory.LoginForAccessTokenAsync(user.Email);
        await NullStartAsync(user);

        using var response = await GetAsync(token, "/api/v1/auth/two-factor");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await StartAsync(user));
    }

    [Fact]
    public async Task PastTheDeadline_AnUnenrolledOwner_IsRefusedWithNoChallenge_ButReachesEnrolment()
    {
        var owner = await SeedAsync();
        var token = await factory.LoginForAccessTokenAsync(owner.Email);
        await SetStartAsync(owner, factory.Clock.GetUtcNow() - PastTheDeadline);

        using var refused = await GetAsync(token);
        using var status = await GetAsync(token, "/api/v1/auth/two-factor");

        AssertGated(refused);
        Assert.Equal(EnrolmentErrorCodes.EnrolmentRequired, await TitleAsync(refused));
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
    }

    // A 403 problem with no challenge, which would loop an MCP client.
    private static void AssertGated(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.WwwAuthenticate.Any());
    }

    // The record gates the consent route: past the deadline, an unenrolled
    // Owner can neither read the consent screen nor approve with a grant.
    [Fact]
    public async Task PastTheDeadline_TheConsentReadAndApprove_AreGated()
    {
        var owner = await SeedAsync();
        var jwt = await factory.LoginForAccessTokenAsync(owner.Email);
        var grant = await OAuthServerTests.StepUpAsync(factory, jwt);
        await SetStartAsync(owner, factory.Clock.GetUtcNow() - PastTheDeadline);
        var query = OAuthServerTests.AuthorizeQuery(
            await OAuthServerTests.RegisterClientAsync(factory.Services), OAuthServerTests.NewCodeVerifier());

        using var read = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, query);
        using var approve = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, query, stepUp: grant);

        foreach (var response in new[] { read, approve })
        {
            AssertGated(response);
            Assert.Equal(EnrolmentErrorCodes.EnrolmentRequired, await TitleAsync(response));
        }
    }

    // G6.1: one user with both flags changes the password first, is then sent
    // to enrolment, enrols and reaches the app.
    [Fact]
    public async Task AUserWithBothFlags_ChangesThePassword_ThenEnrols_ThenReachesTheApp()
    {
        var owner = await SeedAsync();
        var email = $"gate-both-{Guid.NewGuid():N}@test.local";
        var user = new Seeded(email, owner.AccountId, await factory.SeedUserPendingPasswordChangeAsync(owner.AccountId, email));
        await SetStartAsync(user, factory.Clock.GetUtcNow() - PastTheDeadline);
        var first = await factory.LoginForAccessTokenAsync(email);

        using var passwordGate = await GetAsync(first);
        var newPassword = $"Aa1!{Guid.NewGuid():N}";
        var changed = await AccessTokenAsync(await PostAsync(first, "/api/v1/auth/change-password",
            new { currentPassword = TestHarness.Password, newPassword }));
        using var enrolmentGate = await GetAsync(changed);
        var grant = (await JsonAsync(await PostAsync(changed, "/api/v1/auth/step-up", new { password = newPassword })))
            .GetProperty("token").GetString()!;
        var key = (await JsonAsync(await PostAsync(changed, "/api/v1/auth/two-factor/start", null, grant))).GetProperty("key").GetString()!;
        var code = TotpMatcher.Compute(TotpMatcher.DecodeBase32(key), TotpMatcher.StepAt(factory.Clock.GetUtcNow())).ToString("D6");
        var enrolled = await AccessTokenAsync(await PostAsync(changed, "/api/v1/auth/two-factor/confirm", new { code }));
        using var reached = await GetAsync(enrolled);

        AssertGated(passwordGate);
        Assert.Equal("Auth.MustChangePassword", await TitleAsync(passwordGate));
        AssertGated(enrolmentGate);
        Assert.Equal(EnrolmentErrorCodes.EnrolmentRequired, await TitleAsync(enrolmentGate));
        Assert.Equal(HttpStatusCode.OK, reached.StatusCode);
    }

    private async Task<HttpResponseMessage> PostAsync(string bearer, string path, object? body, string? grant = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (grant is not null) request.Headers.Add(AuthEndpoints.StepUpHeaderName, grant);
        return await factory.CreateAuthedClient(bearer).SendAsync(request);
    }

    private static async Task<System.Text.Json.JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage!.RequestUri} answered {(int)response.StatusCode}");
        return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
    }

    private static async Task<string> AccessTokenAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).GetProperty("accessToken").GetString()!;

    // The guarded statement moves only a null start.
    [Fact]
    public async Task StartAsync_LeavesASetStart()
    {
        var owner = await SeedAsync();
        var set = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        await SetStartAsync(owner, set);

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IOwnerGraceStart>()
                .StartAsync(owner.AccountId, owner.UserId, factory.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(set, await StartAsync(owner));
    }

    [Fact]
    public async Task PastTheDeadline_AnEnrolledOwner_IsNotRefused()
    {
        var owner = await SeedAsync();
        await factory.EnrolTwoFactorAsync(owner.Email);
        var token = await factory.LoginForAccessTokenAsync(owner.Email);
        await SetStartAsync(owner, factory.Clock.GetUtcNow() - PastTheDeadline);

        using var response = await GetAsync(token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // G6.1: past the deadline an unenrolled Owner reaches exactly the open
    // routes; before it, the gate refuses nothing.
    [Fact]
    public async Task AWalkOfEveryAuthorizedRoute_ReachesOnlyTheOpenRoutes_PastTheDeadline()
    {
        var owner = await SeedAsync();
        var token = await factory.LoginForAccessTokenAsync(owner.Email);
        var routes = AuthorizedRoutes();
        Assert.True(routes.Count >= 50, $"the walk found {routes.Count} authorized routes, below its floor of 50");

        var gatedBefore = await GatedRoutesAsync(token, routes);
        await SetStartAsync(owner, factory.Clock.GetUtcNow() - PastTheDeadline);
        var gatedAfter = await GatedRoutesAsync(token, routes);

        Assert.True(gatedBefore.Count == 0, "the gate refused before the deadline:\n" + string.Join("\n", gatedBefore));
        var reached = routes.Select(r => $"{r.Method} {r.Path}").Except(gatedAfter).Order().ToList();
        Assert.Equal(OpenRoutes.Order(), reached);
    }

    private async Task<List<string>> GatedRoutesAsync(string token, List<(string Method, string Path)> routes)
    {
        var client = factory.CreateAuthedClient(token);
        var gated = new List<string>();
        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method != "GET") request.Content = JsonContent.Create(new { });
            using var response = await client.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Forbidden
                && await TitleAsync(response) == EnrolmentErrorCodes.EnrolmentRequired)
                gated.Add($"{method} {path}");
        }
        return gated;
    }

    // A session bearer never authenticates on a connected-app route, so the
    // walk leaves those out; G6.2 covers /mcp with a connected-app token.
    private List<(string Method, string Path)> AuthorizedRoutes() =>
        [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is not null && e.Metadata.GetMetadata<IAllowAnonymous>() is null
                && !OAuthEndpoints.AcceptsOAuthTokens(e))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods ? methods : ["GET"]).Select(method => (
                Method: method,
                Path: "/" + string.Join('/', e.RoutePattern.RawText!.Trim('/').Split('/').Select(segment =>
                    segment.StartsWith('{') ? Guid.NewGuid().ToString() : segment)))))];

    private async Task ChangeRoleAsync(Seeded user, string? role)
    {
        var ownerId = await factory.WithTenantScopeAsync(user.AccountId, db =>
            db.Users.Where(u => u.AccountId == user.AccountId && u.Email!.StartsWith("gate-owner-")).Select(u => u.Id).SingleAsync());
        using var scope = factory.Services.CreateScope().ResolveTenantAndActor(user.AccountId, ownerId);
        var changed = await scope.ServiceProvider.GetRequiredService<IIdentityProvider>()
            .ChangeUserRoleAsync(user.AccountId, user.UserId, role, ownerId);
        Assert.True(changed.IsSuccess, changed.IsFailure ? changed.Error.Code : null);
    }

    // G6.3: a promotion starts the grace at the promoted user's first request,
    // a demotion lifts the gate and keeps the start, and a re-promoted former
    // Owner whose start is older than the grace is gated at once.
    [Fact]
    public async Task APromotion_StartsTheGraceAtTheFirstRequest_ADemotionKeepsIt_AndAReturnIsGatedAtOnce()
    {
        var user = await SeedAsync(Roles.Manager);
        await NullStartAsync(user);
        await ChangeRoleAsync(user, Roles.Owner);
        Assert.Null(await StartAsync(user));

        using var promoted = await GetAsync(await factory.LoginForAccessTokenAsync(user.Email));
        var started = await StartAsync(user);
        await SetStartAsync(user, factory.Clock.GetUtcNow() - PastTheDeadline);
        var expired = await StartAsync(user);
        await ChangeRoleAsync(user, Roles.Manager);
        var demotedToken = await factory.LoginForAccessTokenAsync(user.Email);
        using var demoted = await GetAsync(demotedToken, "/api/v1/flocks");
        await ChangeRoleAsync(user, Roles.Owner);
        using var returned = await GetAsync(await factory.LoginForAccessTokenAsync(user.Email));

        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        Assert.NotNull(started);
        Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, returned.StatusCode);
        Assert.Equal(EnrolmentErrorCodes.EnrolmentRequired, await TitleAsync(returned));
        Assert.Equal(expired, await StartAsync(user));
    }

    // G6.7: an Owner reset of another Owner's two-factor keeps the start, so
    // a reset Owner past the deadline enrols again at the next request.
    [Fact]
    public async Task AResetOfAnOwnersTwoFactor_KeepsTheStart()
    {
        var owner = await SeedAsync();
        var other = new Seeded($"gate-second-{Guid.NewGuid():N}@test.local", owner.AccountId, Guid.Empty);
        await factory.SeedUserAsync(owner.AccountId, other.Email, Roles.Owner);
        other = other with { UserId = await factory.WithTenantScopeAsync(owner.AccountId, db =>
            db.Users.Where(u => u.Email == other.Email).Select(u => u.Id).SingleAsync()) };
        await factory.EnrolTwoFactorAsync(other.Email);
        // A whole second, so the value reads back equal from timestamptz.
        var start = DateTimeOffset.FromUnixTimeSeconds((factory.Clock.GetUtcNow() - PastTheDeadline).ToUnixTimeSeconds());
        await SetStartAsync(other, start);

        using (var scope = factory.Services.CreateScope().ResolveTenantAndActor(owner.AccountId, owner.UserId))
        {
            var reset = await scope.ServiceProvider.GetRequiredService<IIdentityProvider>()
                .ResetUserTwoFactorAsync(owner.AccountId, other.UserId, owner.UserId);
            Assert.True(reset.IsSuccess);
        }
        factory.EnrolledFactors.TryRemove(other.Email, out _);
        using var refused = await GetAsync(await factory.LoginForAccessTokenAsync(other.Email));

        Assert.Equal(start, await StartAsync(other));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    // G6.2: a connected-app token minted before the start was set starts the
    // grace, is accepted on /mcp before the deadline and refused past it with
    // no challenge; an enrolled Owner's token is not refused.
    [Fact]
    public async Task AConnectedAppToken_StartsTheGrace_AndIsGatedOnMcpPastTheDeadline()
    {
        var owner = await SeedAsync();
        var app = await ConnectedAppToken.IssueAsync(factory, await factory.LoginForAccessTokenAsync(owner.Email));
        await NullStartAsync(owner);

        using var inGrace = await McpConnection.PostAsync(factory, app, McpConnection.Initialize);
        var started = await StartAsync(owner);
        await SetStartAsync(owner, factory.Clock.GetUtcNow() - PastTheDeadline);
        using var refused = await McpConnection.PostAsync(factory, app, McpConnection.Initialize);

        Assert.Equal(HttpStatusCode.OK, inGrace.StatusCode);
        Assert.NotNull(started);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(EnrolmentErrorCodes.EnrolmentRequired, await TitleAsync(refused));
        Assert.False(refused.Headers.WwwAuthenticate.Any());
    }

    [Fact]
    public async Task AnEnrolledOwnersConnectedAppToken_IsNotRefusedPastTheDeadline()
    {
        var owner = await SeedAsync();
        await factory.EnrolTwoFactorAsync(owner.Email);
        var app = await ConnectedAppToken.IssueAsync(factory, await factory.LoginForAccessTokenAsync(owner.Email),
            code: factory.EnrolledFactors[owner.Email].RecoveryCodes[0]);
        await SetStartAsync(owner, factory.Clock.GetUtcNow() - PastTheDeadline);

        using var response = await McpConnection.PostAsync(factory, app, McpConnection.Initialize);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // G6.6: past the deadline, an enrolled Owner who signs in and steps up
    // through the shared helpers reaches a read, a write, an Owner-only
    // step-up action, consent and /mcp. LoginAsync's helper asserts the
    // password step's pending 401 before it completes the code.
    [Fact]
    public async Task AnEnrolledOwner_ThroughTheSharedHelpers_ReachesTheOwnerEndpoints()
    {
        var owner = await SeedAsync();
        var factor = await factory.EnrolTwoFactorAsync(owner.Email);
        await SetStartAsync(owner, factory.Clock.GetUtcNow() - PastTheDeadline);
        var jwt = await factory.LoginForAccessTokenAsync(owner.Email);
        var client = factory.CreateAuthedClient(jwt);

        using var read = await client.GetAsync("/api/v1/users");
        using var write = await client.PutWithKeyAsync("/api/v1/me/language", Guid.NewGuid().ToString(), new { language = "es" });
        using var createUser = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users")
        {
            Content = JsonContent.Create(new { email = $"gate-new-{Guid.NewGuid():N}@test.local", password = $"Aa1!{Guid.NewGuid():N}", role = Roles.ReadOnly }),
        };
        createUser.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        createUser.Headers.Add(AuthEndpoints.StepUpHeaderName, await OAuthServerTests.StepUpAsync(factory, jwt, factor.RecoveryCodes[0]));
        using var created = await client.SendAsync(createUser);
        var app = await ConnectedAppToken.IssueAsync(factory, jwt, factor.RecoveryCodes[1]);
        using var mcp = await McpConnection.PostAsync(factory, app, McpConnection.Initialize);

        Assert.Equal("true", new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(jwt)
            .Claims.Single(c => c.Type == "two_factor").Value);
        Assert.All([read, write, created, mcp], r => Assert.True(r.IsSuccessStatusCode, $"{r.RequestMessage!.RequestUri} answered {(int)r.StatusCode}"));
    }

    // Decision 18: a failed start lets each request through with one warning
    // and no user id; the next request that can write sets the value.
    [Fact]
    public async Task AFailingStart_PassesTheRequest_LogsOneEventPerRequest_AndTheNextRequestSetsIt()
    {
        var failing = new FailingSwitch { Failing = true };
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var real = services.Last(d => d.ServiceType == typeof(IOwnerGraceStart));
            services.AddScoped<IOwnerGraceStart>(sp => new SwitchableGraceStart((IOwnerGraceStart)ActivatorUtilities.CreateInstance(sp, real.ImplementationType!), failing));
        }));
        var owner = await SeedAsync();
        var token = await factory.LoginForAccessTokenAsync(owner.Email);
        await NullStartAsync(owner);
        var before = factory.EventsFor(SecurityEvents.OwnerGraceStartFailed).Count;

        var authed = host.CreateClient(TestHarness.Cookieless(factory));
        authed.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var first = await authed.GetAsync("/api/v1/users");
        using var second = await authed.GetAsync("/api/v1/users");
        var events = factory.EventsFor(SecurityEvents.OwnerGraceStartFailed).Skip(before).ToList();
        var stillNull = await StartAsync(owner);
        failing.Failing = false;
        using var third = await authed.GetAsync("/api/v1/users");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(LogEventLevel.Warning, e.Level);
            // AccountId is the farm, which every event inside a signed-in request carries (#216).
            Assert.Equal(["ClientIp", "SecurityEvent"], e.Properties.Keys
                .Where(k => k is not ("RequestId" or "RequestPath" or "SourceContext" or "AccountId")).Order());
            Assert.DoesNotContain(owner.UserId.ToString(), e.RenderMessage(), StringComparison.OrdinalIgnoreCase);
        });
        Assert.Null(stillNull);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.NotNull(await StartAsync(owner));
    }

    private sealed class FailingSwitch
    {
        public bool Failing { get; set; }
    }

    private sealed class SwitchableGraceStart(IOwnerGraceStart real, FailingSwitch failing) : IOwnerGraceStart
    {
        public Task StartAsync(Guid accountId, Guid userId, DateTimeOffset now, CancellationToken ct) =>
            failing.Failing ? throw new InvalidOperationException("the grace start failed") : real.StartAsync(accountId, userId, now, ct);
    }

    // Raising the setting and restarting moves an existing deadline out.
    [Fact]
    public async Task RaisingTheGraceDays_MovesAnExistingDeadlineOut()
    {
        var owner = await SeedAsync();
        var token = await factory.LoginForAccessTokenAsync(owner.Email);
        await SetStartAsync(owner, factory.Clock.GetUtcNow() - PastTheDeadline);
        using var raised = factory.WithWebHostBuilder(builder => builder.UseSetting("TwoFactor:OwnerGraceDays", "30"));
        var raisedClient = raised.CreateClient(TestHarness.Cookieless(factory));
        raisedClient.DefaultRequestHeaders.Authorization = new("Bearer", token);

        using var refused = await GetAsync(token);
        using var allowed = await raisedClient.GetAsync("/api/v1/users");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    // The status route tells an Owner the deadline, and nobody else.
    [Fact]
    public async Task TheStatusRoute_ReturnsRequiredByToAnOwner_AndOmitsItForAManager()
    {
        var owner = await SeedAsync();
        var manager = await SeedAsync(Roles.Manager);
        var start = new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero);
        await SetStartAsync(owner, start);
        await SetStartAsync(manager, start);

        using var ownerStatus = await GetAsync(await factory.LoginForAccessTokenAsync(owner.Email), "/api/v1/auth/two-factor");
        using var managerStatus = await GetAsync(await factory.LoginForAccessTokenAsync(manager.Email), "/api/v1/auth/two-factor");

        var ownerBody = await ownerStatus.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var managerBody = await managerStatus.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(start.AddDays(OwnerGraceOptions.DefaultDays), ownerBody.GetProperty("requiredBy").GetDateTimeOffset());
        Assert.False(managerBody.TryGetProperty("requiredBy", out _));
    }

    // G6.7: the setting is a whole number from 1 to 365, checked at boot.
    [Theory]
    [InlineData("0")]
    [InlineData("366")]
    [InlineData("1.5")]
    [InlineData("fourteen")]
    [InlineData("")]
    public void AnOutOfRangeOrFractionalGrace_FailsTheBoot(string days)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("TwoFactor:OwnerGraceDays", days));
        var thrown = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        // The guard's own refusal, not an unrelated failure further on.
        var chain = new List<Exception>();
        for (Exception? e = thrown; e is not null; e = e.InnerException) chain.Add(e);
        Assert.Contains(chain, e => e is InvalidOperationException && e.Message.Contains(
            $"TwoFactor:OwnerGraceDays must be a whole number from 1 to 365, and '{days}' is not.", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnsetGrace_IsFourteenDays_AndTheShippedSettingsLeaveItUnset()
    {
        Assert.Equal(14, factory.Services.GetRequiredService<IOptions<OwnerGraceOptions>>().Value.OwnerGraceDays);
        // Every appsettings*.json in the API project.
        var shipped = Directory.GetFiles(Path.Combine(RepoRoot(), "src", "Cluckwork.Api"), "appsettings*.json");
        Assert.True(shipped.Length >= 3, $"found {shipped.Length} shipped settings files, below the floor of 3");
        Assert.All(shipped, file => Assert.DoesNotContain("OwnerGraceDays", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("30", 1)]
    [InlineData("14", 0)]
    [InlineData("1", 0)]
    public void AGraceAboveFourteen_LogsOneWarningAtStartup(string days, int expected)
    {
        var before = factory.Sink.Events.Count;
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("TwoFactor:OwnerGraceDays", days));
        host.CreateClient().Dispose();

        var warnings = factory.Sink.Events.Skip(before)
            .Count(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.StartsWith("Owner two-factor grace extended", StringComparison.Ordinal));
        Assert.Equal(expected, warnings);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cluckwork.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
