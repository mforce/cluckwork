using System.Net;
using Cluckwork.Api.Configuration;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.IntegrationTests.Mcp;
using Cluckwork.Api.Modules.Access.OAuth;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
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

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(EnrolmentErrorCodes.EnrolmentRequired, await TitleAsync(refused));
        Assert.False(refused.Headers.WwwAuthenticate.Any());
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
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

    // Decision 18: a failed start lets each request through with one warning
    // and no user id; the next request that can write sets the value.
    [Fact]
    public async Task AFailingStart_PassesTheRequest_LogsOneEventPerRequest_AndTheNextRequestSetsIt()
    {
        var failing = new FailingSwitch { Failing = true };
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var real = services.Last(d => d.ServiceType == typeof(IOwnerGraceStart));
            services.AddScoped<IOwnerGraceStart>(sp => new SwitchableGraceStart((IOwnerGraceStart)real.ImplementationFactory!(sp), failing));
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
    public void AnOutOfRangeOrFractionalGrace_FailsTheBoot(string days)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("TwoFactor:OwnerGraceDays", days));
        Assert.ThrowsAny<Exception>(() => host.CreateClient());
    }

    [Fact]
    public void AnUnsetGrace_IsFourteenDays_AndTheShippedSettingsLeaveItUnset()
    {
        Assert.Equal(14, factory.Services.GetRequiredService<IOptions<OwnerGraceOptions>>().Value.OwnerGraceDays);
        var shipped = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cluckwork.Api", "appsettings.json"));
        Assert.DoesNotContain("OwnerGraceDays", shipped, StringComparison.Ordinal);
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
