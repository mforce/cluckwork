using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Jobs;
using Cluckwork.Infrastructure.OAuth;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.IntegrationTests;

// #1148 — a client whose client_id is the https URL of its metadata document. It is
// fetched through the scripted network (never a real host), stored as an ordinary
// application row, and from then on is connected, listed, audited, disconnected,
// switched off and purged exactly like a registered client.
public sealed partial class OAuthFailClosedTests
{
    [Fact]
    public async Task MetadataClient_ConnectsListsAuditsAndDisconnects_LikeAnyOther()
    {
        await using var metadata = await MetadataHostAsync();
        var user = await SeedAsync(Roles.Manager);
        var url = NewDocumentUrl();

        var app = await ConnectAsync(metadata.Host, user, [ReadScope], clientId: url);
        using var read = await Client(metadata.Host, app.Token).GetAsync(Probe.Read);
        var listed = await Client(metadata.Host, user.Jwt).GetFromJsonAsync<JsonElement>("/api/v1/me/connected-apps");
        using var disconnected = await DisconnectAsync(metadata.Host, user.Jwt,
            $"/api/v1/me/connected-apps?clientId={Uri.EscapeDataString(url)}");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var entry = Assert.Single(listed.EnumerateArray());
        Assert.Equal(url, entry.GetProperty("clientId").GetString());
        Assert.Equal("Doc Client", entry.GetProperty("appName").GetString());
        Assert.Equal([(AuditActions.UserAppConnected, user.Id, url, ReadScope)], await ConnectionAuditAsync(user));
        Assert.Equal(HttpStatusCode.NoContent, disconnected.StatusCode);
        using var after = await Client(metadata.Host, app.Token).GetAsync(Probe.Read);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task MetadataClient_IsRefused_WhenTheFarmTurnsAppsOff()
    {
        await using var metadata = await MetadataHostAsync();
        var user = await SeedAsync(Roles.Manager);
        var app = await ConnectAsync(metadata.Host, user, [ReadScope], clientId: NewDocumentUrl());

        await SwitchConnectedAppsAsync(user, on: false);

        await AssertRefusedAsync(metadata.Host, app.Token, "Auth.ConnectedAppsOff");
    }

    // The domain is what Cluckwork checked; the name is still the app's own claim.
    [Fact]
    public async Task Consent_AndPreview_NameTheVerifiedDomain()
    {
        await using var metadata = await MetadataHostAsync();
        var user = await SeedAsync(Roles.Manager);
        var query = AuthorizeParameters(NewDocumentUrl(), NewVerifier(), ReadScope);

        using var consent = await OAuthServerTests.SendAuthorizeAsync(metadata.Host, user.Jwt, query);
        using var preview = await OAuthServerTests.SendAuthorizeAsync(metadata.Host, jwt: null, query, consent: "preview");

        var body = await consent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("app.test", body.GetProperty("verifiedDomain").GetString());
        Assert.Equal("Doc Client", body.GetProperty("clientName").GetString());
        var anonymous = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("app.test", anonymous.GetProperty("verifiedDomain").GetString());
        Assert.Equal("Doc Client", anonymous.GetProperty("clientName").GetString());
    }

    [Fact]
    public async Task StoredCopy_IsReused_UntilItExpires()
    {
        await using var metadata = await MetadataHostAsync();
        var user = await SeedAsync(Roles.Manager);
        var url = NewDocumentUrl();

        await AskConsentAsync(metadata, user, url);
        await AskConsentAsync(metadata, user, url);
        Assert.Single(metadata.Server.Requests);

        await ExpireStoredCopyAsync(url);
        await AskConsentAsync(metadata, user, url);
        Assert.Equal(2, metadata.Server.Requests.Count);
    }

    // A refetch replaces what the stored copy allowed: the document's new redirect URI
    // works and the one it dropped no longer does.
    [Fact]
    public async Task Refetch_AppliesTheNewDocument()
    {
        await using var metadata = await MetadataHostAsync();
        var user = await SeedAsync(Roles.Manager);
        var url = NewDocumentUrl();
        await AskConsentAsync(metadata, user, url);

        const string moved = "https://client.example/moved";
        metadata.Server.Respond = metadata.Server.Serves(MetadataDocumentServer.Document(url, moved));
        await ExpireStoredCopyAsync(url);
        var query = AuthorizeParameters(url, NewVerifier(), ReadScope);
        using var old = await OAuthServerTests.SendAuthorizeAsync(metadata.Host, user.Jwt, query);
        query["redirect_uri"] = moved;
        using var updated = await OAuthServerTests.SendAuthorizeAsync(metadata.Host, user.Jwt, query);

        Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
    }

    // A failed fetch or a document that breaks a rule leaves nothing behind (draft §5.2).
    [Theory]
    [InlineData(404, "application/json")]
    [InlineData(200, "text/html")]
    public async Task UnusableDocument_IsRefused_AndNotStored(int status, string contentType)
    {
        await using var metadata = await MetadataHostAsync();
        var user = await SeedAsync(Roles.Manager);
        var url = NewDocumentUrl();
        metadata.Server.Respond = metadata.Server.Serves(MetadataDocumentServer.Document(url, RedirectUri), contentType, status);

        using var response = await OAuthServerTests.SendAuthorizeAsync(
            metadata.Host, user.Jwt, AuthorizeParameters(url, NewVerifier(), ReadScope));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Errors.InvalidRequest, await AuthorizeErrorOf(response));
        Assert.False(await StoredAsync(url), "an unusable document was stored");
    }

    // OpenIddict's own redirect URI checks still run on what is stored, as for DCR.
    [Fact]
    public async Task DocumentWithAnIssuerInItsRedirect_IsRefused_AndNotStored()
    {
        await using var metadata = await MetadataHostAsync();
        var user = await SeedAsync(Roles.Manager);
        var url = NewDocumentUrl();
        const string fixation = RedirectUri + "?iss=https%3A%2F%2Fevil.example";
        metadata.Server.Respond = metadata.Server.Serves(MetadataDocumentServer.Document(url, fixation));
        var query = AuthorizeParameters(url, NewVerifier(), ReadScope);
        query["redirect_uri"] = fixation;

        using var response = await OAuthServerTests.SendAuthorizeAsync(metadata.Host, user.Jwt, query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Errors.InvalidRequest, await AuthorizeErrorOf(response));
        Assert.False(await StoredAsync(url), "a redirect carrying iss was stored");
    }

    [Fact]
    public async Task DocumentNamingAnotherClient_IsRefused_AndNotStored()
    {
        await using var metadata = await MetadataHostAsync();
        var user = await SeedAsync(Roles.Manager);
        var url = NewDocumentUrl();
        metadata.Server.Respond = metadata.Server.Serves(MetadataDocumentServer.Document(url + "x", RedirectUri));

        using var response = await OAuthServerTests.SendAuthorizeAsync(
            metadata.Host, user.Jwt, AuthorizeParameters(url, NewVerifier(), ReadScope));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await StoredAsync(url), "a document naming another client was stored");
    }

    // A URL that keeps failing is not fetched more than its budget allows, however often
    // the authorization request is repeated.
    [Fact]
    public async Task FailingUrl_IsFetchedOnlyWithinItsBudget()
    {
        await using var metadata = await MetadataHostAsync();
        var user = await SeedAsync(Roles.Manager);
        var url = NewDocumentUrl();
        metadata.Server.Respond = metadata.Server.Serves("", status: 500);
        var query = AuthorizeParameters(url, NewVerifier(), ReadScope);

        string? last = null;
        for (var attempt = 0; attempt <= ClientMetadataDocuments.PerUrlBudget.Limit; attempt++)
        {
            using var response = await OAuthServerTests.SendAuthorizeAsync(metadata.Host, user.Jwt, query);
            last = await AuthorizeErrorOf(response);
        }

        Assert.Equal(ClientMetadataDocuments.PerUrlBudget.Limit, metadata.Server.Requests.Count);
        Assert.Equal(Errors.TemporarilyUnavailable, last);
    }

    // Many different URLs share one budget, so the server cannot be made to hit hosts fast.
    // Draining it locks out only clients never seen before: an app whose stored copy has
    // expired keeps working on that copy until a fetch is possible again.
    [Fact]
    public async Task ManyUrls_ShareOneGlobalBudget_AndAStoredCopyOutlastsIt()
    {
        await using var metadata = await MetadataHostAsync();
        var known = NewDocumentUrl();
        metadata.Server.Respond = context => context.Request.Path == new Uri(known).AbsolutePath
            ? metadata.Server.Serves(MetadataDocumentServer.Document(known, RedirectUri))(context)
            : metadata.Server.Serves("", status: 404)(context);
        using var fetcher = metadata.Network.Fetcher();
        var resolver = new ClientMetadataDocuments(new ClientMetadataOptions(), metadata.Host.Services.GetRequiredService<IServiceScopeFactory>(),
            fetcher, new InProcessFixedWindowCounter(TimeProvider.System), TimeProvider.System, NullLogger<ClientMetadataDocuments>.Instance);
        Assert.True((await resolver.EnsureFreshAsync(known, CancellationToken.None)).IsSuccess);
        await ExpireStoredCopyAsync(known);

        var outcomes = new List<string>();
        for (var i = 1; i <= ClientMetadataDocuments.GlobalBudget.Limit; i++)
            outcomes.Add((await resolver.EnsureFreshAsync(NewDocumentUrl(), CancellationToken.None)).Error.Code);
        var stale = await resolver.EnsureFreshAsync(known, CancellationToken.None);

        Assert.Equal(ClientMetadataDocuments.GlobalBudget.Limit, metadata.Server.Requests.Count);
        Assert.All(outcomes[..^1], code => Assert.Equal(Errors.InvalidRequest, code));
        Assert.Equal(Errors.TemporarilyUnavailable, outcomes[^1]);
        Assert.True(stale.IsSuccess, "an expired copy was refused while the budget was spent");
    }

    // Two first requests race to store the same document; both proceed and one row stays.
    [Fact]
    public async Task ConcurrentFirstRequests_StoreOneRow()
    {
        await using var metadata = await MetadataHostAsync();
        var user = await SeedAsync(Roles.Manager);
        var url = NewDocumentUrl();
        var arrived = 0;
        var bothArrived = new TaskCompletionSource();
        var serve = metadata.Server.Respond;
        metadata.Server.Respond = async context =>
        {
            if (Interlocked.Increment(ref arrived) == 2) bothArrived.TrySetResult();
            await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });
            await serve(context);
        };
        var query = AuthorizeParameters(url, NewVerifier(), ReadScope);

        var responses = await Task.WhenAll(
            OAuthServerTests.SendAuthorizeAsync(metadata.Host, user.Jwt, query),
            OAuthServerTests.SendAuthorizeAsync(metadata.Host, user.Jwt, query));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(1, await factory.WithTenantScopeAsync(user.AccountId, db =>
            db.OAuthApplications.CountAsync(application => application.ClientId == url)));
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task Discovery_AdvertisesMetadataDocuments()
    {
        await using var metadata = await MetadataHostAsync();

        var discovery = await Client(metadata.Host, bearer: null)
            .GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");

        Assert.True(discovery.GetProperty("client_id_metadata_document_supported").GetBoolean());
    }

    // A deployment without outbound https turns them off: discovery stops offering them,
    // nothing is fetched, and a copy stored earlier stops working too.
    [Fact]
    public async Task TurnedOff_NeitherAdvertisesNorFetches()
    {
        await using var on = await MetadataHostAsync();
        var user = await SeedAsync(Roles.Manager);
        var url = NewDocumentUrl();
        await AskConsentAsync(on, user, url);
        await using var off = await MetadataHostAsync(enabled: false);

        var discovery = await Client(off.Host, bearer: null)
            .GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");
        using var response = await OAuthServerTests.SendAuthorizeAsync(
            off.Host, user.Jwt, AuthorizeParameters(url, NewVerifier(), ReadScope));

        Assert.False(discovery.TryGetProperty("client_id_metadata_document_supported", out _));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(off.Network.Resolves);
    }

    // An unapproved metadata client is purged like an unapproved registration; the next
    // request simply fetches it again.
    [Fact]
    public async Task UnapprovedMetadataClient_IsPurged_AndFetchedAgainOnNextUse()
    {
        await using var metadata = await MetadataHostAsync();
        var user = await SeedAsync(Roles.Manager);
        var url = NewDocumentUrl();
        await AskConsentAsync(metadata, user, url);

        await AgeMetadataClientAsync(url, OAuthPurgeSweep.UnapprovedWindow + TimeSpan.FromHours(1));
        await using (var scope = metadata.Host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IOAuthPurge>().PurgeAsync(
                DateTimeOffset.UtcNow - OAuthPurgeSweep.PruneRetention, OAuthPurgeSweep.UnapprovedWindow, CancellationToken.None);
        Assert.False(await StoredAsync(url), "the purge kept an unapproved metadata client");

        await AskConsentAsync(metadata, user, url);
        Assert.True(await StoredAsync(url), "the next request did not fetch the document again");
    }

    private sealed class MetadataHost(WebApplicationFactory<Program> host, MetadataDocumentServer server, FakeNetwork network)
        : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Host => host;
        public MetadataDocumentServer Server => server;
        public FakeNetwork Network => network;

        public async ValueTask DisposeAsync()
        {
            await host.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    // Serves a valid document for whichever path is asked, redirecting to RedirectUri.
    private async Task<MetadataHost> MetadataHostAsync(bool enabled = true)
    {
        var server = await MetadataDocumentServer.StartAsync();
        server.Respond = context => server.Serves(
            MetadataDocumentServer.Document("https://app.test" + context.Request.Path, RedirectUri))(context);
        var network = new FakeNetwork(server);
        var host = Host().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OAuth:ClientMetadata:Enabled", enabled.ToString());
            builder.ConfigureTestServices(services => services.AddSingleton(_ => network.Fetcher()));
        });
        return new MetadataHost(host, server, network);
    }

    // OpenIddict answers an authorization request it cannot send back to the client in
    // plain text, one "name:value" line per parameter.
    private static async Task<string?> AuthorizeErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadAsStringAsync()).Split('\n')
            .FirstOrDefault(line => line.StartsWith("error:", StringComparison.Ordinal))?["error:".Length..].Trim();

    private static string NewDocumentUrl() => $"https://app.test/{Guid.NewGuid():N}/client.json";

    private static async Task AskConsentAsync(MetadataHost metadata, SeededUser user, string url)
    {
        using var response = await OAuthServerTests.SendAuthorizeAsync(
            metadata.Host, user.Jwt, AuthorizeParameters(url, NewVerifier(), ReadScope));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<bool> StoredAsync(string url)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().OAuthApplications
            .AnyAsync(application => application.ClientId == url);
    }

    private async Task ExpireStoredCopyAsync(string url)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var updated = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync(
            $$"""UPDATE "OpenIddictApplications" SET "Properties" = '{"cimd_expires_at":0}' WHERE "ClientId" = {{url}}""");
        Assert.Equal(1, updated);
    }

    // As OAuthPurgeTests ages a registration: the #819 trigger keeps CreatedAtUtc, so it
    // is switched off inside one transaction.
    private async Task AgeMetadataClientAsync(string url, TimeSpan age)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "OpenIddictApplications" DISABLE TRIGGER "TR_OpenIddictApplications_BusinessRecordTimestamps";""");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "OpenIddictApplications" SET "CreatedAtUtc" = now() - {age} WHERE "ClientId" = {url}""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "OpenIddictApplications" ENABLE TRIGGER "TR_OpenIddictApplications_BusinessRecordTimestamps";""");
        await transaction.CommitAsync();
    }
}
