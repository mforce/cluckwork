using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Mcp;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using static Cluckwork.Api.IntegrationTests.Mcp.McpConnection;
using static Cluckwork.Api.IntegrationTests.Mcp.RecordDailyEntryToolTests;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// #1326 — an app that asked for every scope and was granted some of them: the real consent
// flow and the SDK's client see exactly the granted tools, and a hidden tool's refusal never
// asks the client to sign in again.
public sealed class McpPartialConsentTests(CluckworkWebApplicationFactory factory)
    : IClassFixture<CluckworkWebApplicationFactory>
{
    private static readonly string[] EveryScope = [.. OAuthScopes.All];
    private static readonly string[] ReadTools =
    [
        "cluckwork_list_customer_balances", "cluckwork_list_customers", "cluckwork_list_daily_entries",
        "cluckwork_list_egg_lot_movements", "cluckwork_list_egg_lots", "cluckwork_list_flocks",
        "cluckwork_list_order_payments", "cluckwork_list_sales_orders", "cluckwork_read_stock",
    ];

    [Theory]
    [InlineData(new[] { OAuthScopes.ReadFarm, OAuthScopes.WriteDailyEntries }, new[] { DailyEntryTools.RecordDailyEntry })]
    [InlineData(new[] { OAuthScopes.SubmitDailyEntries }, new[] { DailyEntryTools.SubmitDailyEntry })]
    [InlineData(new[] { OAuthScopes.WriteOperations },
        new[] { InventoryTools.RecordFeedUsage, InventoryTools.RecordWaterUsage, InventoryTools.RecordInventoryPurchase })]
    [InlineData(new[] { OAuthScopes.WriteExpenses }, new[] { ExpenseTools.RecordExpense })]
    public async Task PartialConsent_ListsExactlyTheChosenTools(string[] chosen, string[] writeTools)
    {
        using var host = Host(factory);
        var token = await IssueAsync(host, await SeedAsync(factory, Roles.Manager), EveryScope, selectedScopes: chosen);

        await using var client = await ConnectAsync(host, token);

        string[] expected = [.. chosen.Contains(OAuthScopes.ReadFarm) ? ReadTools : [], .. writeTools];
        Assert.Equal(expected.Order(), (await client.ListToolsAsync()).Select(t => t.Name).Where(n => n.StartsWith("cluckwork_")).Order());
    }

    // MCP clients start re-authorization on a 403 insufficient_scope, so a call to a tool the
    // person did not grant stays a JSON-RPC error and writes nothing.
    [Fact]
    public async Task HiddenToolCall_IsAJsonRpcError_WithoutAScopeChallenge()
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(factory, Roles.Manager);
        var token = await IssueAsync(host, farm.User, EveryScope, selectedScopes: [OAuthScopes.ReadFarm]);
        var call = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 2, method = "tools/call",
            @params = new { name = DailyEntryTools.RecordDailyEntry, arguments = Arguments(farm.FlockName) },
        });

        using var response = await PostAsync(host, token, call);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Contains("\"code\":-32600", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await EntryCountAsync(factory, farm));
    }

    // A narrower approval does not revoke the wider one: its token keeps every tool until
    // Disconnect.
    [Fact]
    public async Task NarrowerApproval_LeavesTheWiderTokenWorking()
    {
        using var host = Host(factory);
        var user = await SeedAsync(factory, Roles.Manager);
        var clientId = await OAuthServerTests.RegisterClientAsync(host.Services);
        var wide = await IssueForAsync(host, user, clientId, EveryScope);
        var narrow = await IssueForAsync(host, user, clientId, [OAuthScopes.ReadFarm]);

        await using var wideClient = await ConnectAsync(host, wide);
        await using var narrowClient = await ConnectAsync(host, narrow);

        Assert.Contains(ExpenseTools.RecordExpense, (await wideClient.ListToolsAsync()).Select(t => t.Name));
        Assert.DoesNotContain(ExpenseTools.RecordExpense, (await narrowClient.ListToolsAsync()).Select(t => t.Name));
    }

    // IssueAsync registers a new app each time; this approves the same app again.
    private static async Task<string> IssueForAsync(
        WebApplicationFactory<Program> host, User user, string clientId, string[] chosen)
    {
        var verifier = OAuthServerTests.NewCodeVerifier();
        var query = AuthorizeQuery(clientId, verifier, EveryScope, resource: null);
        var location = await OAuthServerTests.ApproveAsync(host, user.Jwt, query, chosen);
        var code = QueryHelpers.ParseQuery(location.Query)["code"].ToString();
        using var response = await RedeemAsync(host, clientId, code, verifier, query["redirect_uri"]!, resource: null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }
}
