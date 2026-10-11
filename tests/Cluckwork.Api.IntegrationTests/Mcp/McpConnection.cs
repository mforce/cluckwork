using System.Collections.Concurrent;
using System.ComponentModel;
using System.Data.Common;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Mcp;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// #806 — a connected app reaching /mcp through the real OAuth flow and the real MCP
// client. The probe tools exist only in this test host; the real tree has none yet.
internal static class McpConnection
{
    // The endpoint a client connects to is also the resource its token names (RFC 8707).
    public const string McpUrl = "https://localhost/mcp";
    public const string ResourceMetadata = "https://localhost/.well-known/oauth-protected-resource/mcp";
    // Registered only in this test host, so a token can lack both MCP scopes or name
    // another resource.
    public const string OtherScope = "cw806.other";
    public const string OtherResource = "https://other.example/api";

    public sealed record User(Guid Id, Guid AccountId, string Jwt, string OwnerEmail);

    // alterIssued changes every principal the authorization server signs, so a test can
    // hold a token the real flow no longer mints.
    public static WebApplicationFactory<Program> Host(
        CluckworkWebApplicationFactory factory, Action<ClaimsPrincipal>? alterIssued = null,
        params (string Policy, int PermitLimit)[] limits) =>
        factory.WithWebHostBuilder(builder =>
        {
            foreach (var (policy, permitLimit) in limits)
            {
                builder.UseSetting($"RateLimiting:{policy}:PermitLimit", permitLimit.ToString());
                builder.UseSetting($"RateLimiting:{policy}:WindowSeconds", "86400");
            }
            builder.ConfigureTestServices(services =>
            {
                services.AddMcpServer().WithTools<ProbeTools>();
                services.Configure<OpenIddictServerOptions>(options =>
                {
                    options.Scopes.Add(OtherScope);
                    options.Resources.Add(new Uri(OtherResource));
                });
                if (alterIssued is not null)
                    services.AddOpenIddict().AddServer(server =>
                        server.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(handler => handler
                            .UseInlineHandler(context =>
                            {
                                alterIssued(context.Principal!);
                                return default;
                            })
                            .SetOrder(OpenIddictServerHandlers.InferResources.Descriptor.Order - 250)));
            });
        });

    public static async Task<User> SeedAsync(CluckworkWebApplicationFactory factory, string? role)
    {
        var ownerEmail = $"mcp-owner-{Guid.NewGuid():N}@test.local";
        var email = $"mcp-user-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);
        await factory.SeedUserAsync(accountId, email, role);
        var id = await factory.WithTenantScopeAsync(accountId, db =>
            db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        return new(id, accountId, await factory.LoginForAccessTokenAsync(email), ownerEmail);
    }

    public static async Task<string> IssueAsync(
        WebApplicationFactory<Program> host, User user, string[] scopes, string? resource = null)
    {
        var clientId = await OAuthServerTests.RegisterClientAsync(host.Services);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var query = AuthorizeQuery(clientId, verifier, scopes, resource);
        var location = await OAuthServerTests.ApproveAsync(host, user.Jwt, query);
        var code = QueryHelpers.ParseQuery(location.Query)["code"].ToString();
        using var response = await RedeemAsync(host, clientId, code, verifier, query["redirect_uri"]!, resource);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }

    public static Dictionary<string, string?> AuthorizeQuery(
        string clientId, string verifier, string[] scopes, string? resource)
    {
        var query = OAuthServerTests.AuthorizeQuery(clientId, verifier);
        query["scope"] = string.Join(' ', scopes);
        if (resource is not null)
            query["resource"] = resource;
        return query;
    }

    public static Task<HttpResponseMessage> RedeemAsync(
        WebApplicationFactory<Program> host, string clientId, string code, string verifier,
        string redirectUri, string? resource)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.AuthorizationCode,
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        };
        if (resource is not null)
            form["resource"] = resource;
        return OAuthServerTests.HttpsClient(host, bearer: null).PostAsync("/api/v1/oauth/token", new FormUrlEncodedContent(form));
    }

    public static Task<McpClient> ConnectAsync(
        WebApplicationFactory<Program> host, string token, string? protocolVersion = null, string? origin = null)
    {
        var http = host.CreateDefaultClient(new Uri("https://localhost"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null)
            http.DefaultRequestHeaders.Add("Origin", origin);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(McpUrl), TransportMode = HttpTransportMode.StreamableHttp },
            http, ownsHttpClient: true);
        return McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = protocolVersion });
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static WebApplicationFactory<Program> WithReaderRowCounts(WebApplicationFactory<Program> host, ReaderRowCounts reads) =>
        host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<AppDbContext>((_, options) => options.AddInterceptors(reads))));

    public static async Task<Page<T>> PageAsync<T>(McpClient client, string tool, Dictionary<string, object?>? arguments = null)
    {
        var result = await client.CallToolAsync(tool, arguments);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, text);
        return JsonSerializer.Deserialize<Page<T>>(text, Json)!;
    }

    public static async Task<string> ErrorAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError == true, text);
        return text;
    }

    // McpPage<T> as a client reads it.
    public sealed record Page<T>(List<T> Items, bool Complete, int? NextOffset);

    // Rows each data reader returned, by the table its query selects from.
    public sealed class ReaderRowCounts : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<(string Sql, int Rows)> seen = new();

        public void Clear() => seen.Clear();

        public IReadOnlyList<int> From(string table) =>
            [.. seen.Where(r => r.Sql.Contains($"FROM {table}", StringComparison.Ordinal)).Select(r => r.Rows)];

        public override InterceptionResult DataReaderClosing(
            DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            seen.Enqueue((command.CommandText, eventData.ReadCount));
            return result;
        }

        public override ValueTask<InterceptionResult> DataReaderClosingAsync(
            DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            seen.Enqueue((command.CommandText, eventData.ReadCount));
            return ValueTask.FromResult(result);
        }
    }

    // One raw JSON-RPC message, as a client sends it, so a refusal before the SDK is visible.
    public static Task<HttpResponseMessage> PostAsync(WebApplicationFactory<Program> host, string? bearer, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return OAuthServerTests.HttpsClient(host, bearer).SendAsync(request);
    }

    public const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"cw806","version":"1"}}}""";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Shaped like a real tool: it learns its caller from McpCallContext and carries a role
    // policy beside a scope policy.
    [McpServerToolType]
    public sealed class ProbeTools(McpCallContext call)
    {
        public const string Read = "cw806_read";
        public const string Write = "cw806_write";

        [McpServerTool(Name = Read, ReadOnly = true), Description("Who is calling.")]
        [Authorize]
        [Authorize(Policy = AuthPolicies.FarmReadScope)]
        public string WhoIsCalling() =>
            $"{call.AccountId}/{call.UserId}/{call.ConnectedApp.ClientId}/{call.IsFlockRestricted}/{string.Join(',', call.AssignedFlockIds.Order())}";

        [McpServerTool(Name = Write), Description("A write a Worker may make.")]
        [Authorize(Policy = AuthPolicies.ProductionWrite)]
        [Authorize(Policy = AuthPolicies.DailyEntriesWriteScope)]
        public string Record() => call.UserId.ToString();
    }
}
