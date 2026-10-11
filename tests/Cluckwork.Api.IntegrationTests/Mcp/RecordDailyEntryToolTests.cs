using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Mcp;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Accounts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Domain.Modules.FlockManagement.Contracts;
using Cluckwork.Domain.Modules.EggOperations.Contracts;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static Cluckwork.Api.IntegrationTests.Mcp.McpConnection;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// #809 — cluckwork_record_daily_entry through the real OAuth flow and the SDK's client.
[Collection(IntegrationCollection.Name)]
public sealed class RecordDailyEntryToolTests(CluckworkWebApplicationFactory factory)
{
    private static readonly string[] WriteOnly = [OAuthScopes.WriteDailyEntries];
    private static readonly string[] BothScopes = [OAuthScopes.ReadFarm, OAuthScopes.WriteDailyEntries];
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    [Fact]
    public async Task RecordsADraft_ByFlockAndGradeNames_AndTheAuditRowNamesTheConnectedApp()
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(Roles.Manager);
        await using var client = await ConnectAsync(host, await IssueAsync(host, farm.User, BothScopes));
        var clientId = (await TextAsync(client, ProbeTools.Read)).Split('/')[2];

        var reply = await RecordAsync(client, Arguments(farm.FlockName, grades: [new { grade = "large", quantity = 90 }]));

        var entryId = reply.GetProperty("dailyEntryId").GetGuid();
        Assert.Equal(farm.FlockId, reply.GetProperty("flockId").GetGuid());
        Assert.False(reply.GetProperty("replayed").GetBoolean());
        var stored = await factory.WithTenantScopeAsync(farm.User.AccountId, db => db.DailyEntries
            .Where(e => e.Id == entryId)
            .Select(e => new { e.FarmId, e.HouseId, e.FlockId, e.Status, e.TotalEggs, e.DiscardedEggs, Grade = e.Grades.Single().EggGradeId })
            .SingleAsync());
        Assert.Equivalent(new
        {
            farm.FarmId, farm.HouseId, farm.FlockId, Status = DailyEntryStatus.Draft, TotalEggs = 100, DiscardedEggs = 1,
            Grade = farm.LargeGradeId,
        }, stored);
        var audit = await AuditAsync(farm.User.AccountId, entryId);
        Assert.Equal(new[] { (AuditActions.DailyEntryCreate, (string?)clientId) }, audit);
    }

    // A connection may hold only the write scope; name resolution needs no read tool.
    [Fact]
    public async Task WriteOnlyConnection_Records()
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(Roles.Manager);
        await using var client = await ConnectAsync(host, await IssueAsync(host, farm.User, WriteOnly));

        var reply = await RecordAsync(client, Arguments(farm.FlockName));

        Assert.Equal(farm.FlockId, reply.GetProperty("flockId").GetGuid());
    }

    [Theory]
    [InlineData(Roles.ReadOnly, new[] { OAuthScopes.ReadFarm, OAuthScopes.WriteDailyEntries })]
    [InlineData(Roles.Sales, new[] { OAuthScopes.ReadFarm, OAuthScopes.WriteDailyEntries })]
    [InlineData(Roles.Manager, new[] { OAuthScopes.ReadFarm })]
    public async Task RoleOrScopeWithoutTheWrite_HidesAndRefusesTheTool(string role, string[] scopes)
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(role);
        await using var client = await ConnectAsync(host, await IssueAsync(host, farm.User, scopes));

        var listed = (await client.ListToolsAsync()).Select(t => t.Name);
        var refused = await Assert.ThrowsAsync<McpProtocolException>(() =>
            client.CallToolAsync(DailyEntryTools.RecordDailyEntry, Arguments(farm.FlockName)).AsTask());

        Assert.DoesNotContain(DailyEntryTools.RecordDailyEntry, listed);
        Assert.Equal(McpErrorCode.InvalidRequest, refused.ErrorCode);
        Assert.Equal(0, await EntryCountAsync(farm));
    }

    // Guard 27, with its positive control: the same Worker records for the flock they are
    // assigned to. The scoped read refuses first; FlockScopeGuard in the handler is behind it.
    [Fact]
    public async Task Worker_RecordsForTheirFlock_AndIsRefusedForAnother_ByNameAndById()
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(role: null);
        var assigned = await factory.SeedFlockAsync(farm.User.AccountId, farm.FarmId);
        await factory.WithTenantScopeAsync(farm.User.AccountId, async db =>
        {
            db.UserRoleAssignments.Add(UserRoleAssignment.Create(Guid.NewGuid(), farm.User.AccountId, farm.User.Id, null, null, assigned));
            await db.SaveChangesAsync();
        });
        await using var client = await ConnectAsync(host, await IssueAsync(host, farm.User, WriteOnly));

        var recorded = await RecordAsync(client, Arguments(assigned.ToString()));
        var byName = await CallAsync(client, Arguments(farm.FlockName));
        var byId = await CallAsync(client, Arguments(farm.FlockId.ToString()));

        Assert.Equal(assigned, recorded.GetProperty("flockId").GetGuid());
        Assert.Contains("No flock you can see is named or numbered", ErrorText(byName));
        Assert.Contains("No flock you can see is named or numbered", ErrorText(byId));
        Assert.Equal(0, await EntryCountAsync(farm));
    }

    // Guard 26 for the write: another farm's flock id is not there for this bearer.
    [Fact]
    public async Task AnotherFarmsFlock_IsNotFound_AndNothingIsWritten()
    {
        using var host = Host(factory);
        var victim = await SeedFarmAsync(Roles.Manager);
        var caller = await SeedFarmAsync(Roles.Owner);
        await using var client = await ConnectAsync(host, await IssueAsync(host, caller.User, WriteOnly));

        var result = await CallAsync(client, Arguments(victim.FlockId.ToString()));

        Assert.Contains("No flock you can see is named or numbered", ErrorText(result));
        Assert.Equal(0, await EntryCountAsync(victim));
    }

    [Fact]
    public async Task FarmSwitchOff_RefusesTheNextCall()
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(Roles.Manager);
        var token = await IssueAsync(host, farm.User, WriteOnly);
        await using (var client = await ConnectAsync(host, token))
            await RecordAsync(client, Arguments(farm.FlockName));

        var owner = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(farm.User.OwnerEmail));
        var version = (await owner.GetFromJsonAsync<JsonNode>("/api/v1/account"))!["version"]!.GetValue<int>();
        using var off = new HttpRequestMessage(HttpMethod.Put, "/api/v1/account/connected-apps")
        {
            Content = JsonContent.Create(new { allow = false, version }),
        };
        off.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(off)).StatusCode);

        var call = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 2, method = "tools/call",
            @params = new { name = DailyEntryTools.RecordDailyEntry, arguments = Arguments(farm.FlockName, date: Today.AddDays(-1)) },
        });
        using var response = await PostAsync(host, token, call);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Auth.ConnectedAppsOff", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal(1, await EntryCountAsync(farm));
    }

    // Guard 23. The handler upserts on the natural key, so the entry count cannot tell a
    // replay from a second write; the second write's DailyEntryUpdate row can. The control
    // proves a second execution would leave one.
    [Fact]
    public async Task SameKeyAndArguments_Replays_WithoutASecondAuditRow()
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(Roles.Manager);
        await using var client = await ConnectAsync(host, await IssueAsync(host, farm.User, WriteOnly));
        var arguments = Arguments(farm.FlockName);

        var first = await RecordAsync(client, arguments);
        var replay = await RecordAsync(client, arguments);
        var entryId = first.GetProperty("dailyEntryId").GetGuid();
        var afterReplay = (await AuditAsync(farm.User.AccountId, entryId)).Select(a => a.Action);
        await RecordAsync(client, With(arguments, "idempotencyKey", Guid.NewGuid().ToString()));
        var afterNewKey = (await AuditAsync(farm.User.AccountId, entryId)).Select(a => a.Action);

        Assert.Equal([AuditActions.DailyEntryCreate], afterReplay);
        Assert.Equal([AuditActions.DailyEntryCreate, AuditActions.DailyEntryUpdate], afterNewKey);
        Assert.Equal(entryId, replay.GetProperty("dailyEntryId").GetGuid());
        Assert.True(replay.GetProperty("replayed").GetBoolean());
    }

    // A replay re-checks current flock access. B stays assigned, so the Worker stays
    // restricted (no assignment rows means unrestricted). Once A is unassigned, the stored
    // reply for A is refused exactly as a fresh call is, and nothing is written.
    [Fact]
    public async Task Replay_AfterTheFlockIsUnassigned_IsRefusedLikeAFreshCall()
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(role: null);
        var accountId = farm.User.AccountId;
        var other = await factory.SeedFlockAsync(accountId, farm.FarmId);
        var assignment = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.UserRoleAssignments.Add(UserRoleAssignment.Create(assignment, accountId, farm.User.Id, null, null, farm.FlockId));
            db.UserRoleAssignments.Add(UserRoleAssignment.Create(Guid.NewGuid(), accountId, farm.User.Id, null, null, other));
            await db.SaveChangesAsync();
        });
        await using var client = await ConnectAsync(host, await IssueAsync(host, farm.User, WriteOnly));
        var arguments = Arguments(farm.FlockId.ToString());
        var entryId = (await RecordAsync(client, arguments)).GetProperty("dailyEntryId").GetGuid();

        await factory.WithTenantScopeAsync(accountId,
            db => db.UserRoleAssignments.Where(a => a.Id == assignment).ExecuteDeleteAsync());
        var replay = ErrorText(await CallAsync(client, arguments));
        var fresh = ErrorText(await CallAsync(client, With(arguments, "idempotencyKey", Guid.NewGuid().ToString())));

        Assert.Contains("No flock you can see is named or numbered", replay);
        Assert.Equal(fresh, replay);
        Assert.Equal([AuditActions.DailyEntryCreate], (await AuditAsync(accountId, entryId)).Select(a => a.Action));
    }

    // The role and scope gates run before the unit, so a token without the write scope
    // cannot reach a stored reply either.
    [Fact]
    public async Task Replay_WithATokenLackingTheWriteScope_IsRefusedBeforeTheUnit()
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(Roles.Manager);
        var arguments = Arguments(farm.FlockName);
        await using (var writer = await ConnectAsync(host, await IssueAsync(host, farm.User, WriteOnly)))
            await RecordAsync(writer, arguments);
        await using var reader = await ConnectAsync(host, await IssueAsync(host, farm.User, [OAuthScopes.ReadFarm]));

        var refused = await Assert.ThrowsAsync<McpProtocolException>(() =>
            reader.CallToolAsync(DailyEntryTools.RecordDailyEntry, arguments).AsTask());

        Assert.Equal(McpErrorCode.InvalidRequest, refused.ErrorCode);
    }

    // Guard 22.
    [Fact]
    public async Task SameKey_DifferentArguments_IsRefused_AndChangesNothing()
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(Roles.Manager);
        await using var client = await ConnectAsync(host, await IssueAsync(host, farm.User, WriteOnly));
        var arguments = Arguments(farm.FlockName);
        await RecordAsync(client, arguments);

        var reused = await CallAsync(client, With(arguments, "totalEggs", 250));

        Assert.Contains("Idempotency.KeyReused:", ErrorText(reused));
        Assert.Equal(100, await factory.WithTenantScopeAsync(farm.User.AccountId,
            db => db.DailyEntries.Where(e => e.FlockId == farm.FlockId).Select(e => e.TotalEggs).SingleAsync()));
    }

    // A refusal is not cached: once the farm fixes the flock, the identical call records.
    [Fact]
    public async Task DomainFailure_IsReleased_SoTheSameKeyRecordsOnceFixed()
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(Roles.Manager);
        await SetFlockAsync(farm, FlockStatus.Depleted, Today.AddDays(-1));
        await using var client = await ConnectAsync(host, await IssueAsync(host, farm.User, WriteOnly));
        var arguments = Arguments(farm.FlockName);

        var refused = await CallAsync(client, arguments);
        var claimsAfterRefusal = await factory.WithTenantScopeAsync(farm.User.AccountId,
            db => db.IdempotencyRecords.CountAsync(r => r.AccountId == farm.User.AccountId));
        await SetFlockAsync(farm, FlockStatus.Active, depletedOn: null);
        var recorded = await RecordAsync(client, arguments);

        Assert.Contains("DailyEntry.FlockNotActive:", ErrorText(refused));
        Assert.Equal(0, claimsAfterRefusal);
        Assert.False(recorded.GetProperty("replayed").GetBoolean());
        Assert.Equal(1, await EntryCountAsync(farm));
    }

    // Guard 32: a shared name is refused with every candidate, and an id then records.
    [Fact]
    public async Task SharedFlockName_IsAmbiguous_AndAnIdRecords()
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(Roles.Manager);
        var twin = await factory.SeedFlockAsync(farm.User.AccountId, farm.FarmId);
        await factory.WithTenantScopeAsync(farm.User.AccountId, db =>
            db.Flocks.Where(f => f.Id == twin).ExecuteUpdateAsync(s => s.SetProperty(f => f.Name, farm.FlockName)));
        await using var client = await ConnectAsync(host, await IssueAsync(host, farm.User, WriteOnly));

        var ambiguous = ErrorText(await CallAsync(client, Arguments(farm.FlockName)));
        var recorded = await RecordAsync(client, Arguments(twin.ToString()));

        Assert.Contains("More than one flock is named", ambiguous);
        Assert.Contains(farm.FlockId.ToString(), ambiguous);
        Assert.Contains(twin.ToString(), ambiguous);
        Assert.Equal(twin, recorded.GetProperty("flockId").GetGuid());
    }

    // #1209 caps name resolution at ten candidates. Beyond that the refusal states no count,
    // says more flocks share the name and points to the paged flock list. Nothing is picked.
    [Fact]
    public async Task ElevenFlocksSharingAName_AreRefused_WithThePointerToTheFlockList()
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(Roles.Manager);
        for (var i = 0; i < 10; i++)
            await factory.SeedFlockAsync(farm.User.AccountId, farm.FarmId);
        await factory.WithTenantScopeAsync(farm.User.AccountId, db =>
            db.Flocks.ExecuteUpdateAsync(s => s.SetProperty(f => f.Name, farm.FlockName)));
        await using var client = await ConnectAsync(host, await IssueAsync(host, farm.User, WriteOnly));

        var refused = ErrorText(await CallAsync(client, Arguments(farm.FlockName)));

        Assert.Contains("More flocks share this name than are listed", refused);
        Assert.Contains("cluckwork_list_flocks", refused);
        Assert.Equal(10, System.Text.RegularExpressions.Regex.Count(refused, "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"));
        Assert.Equal(0, await factory.WithTenantScopeAsync(farm.User.AccountId, db => db.DailyEntries.CountAsync()));
    }

    // Guard 25: the validator and handler behind the contract decide, not the tool.
    [Theory]
    [InlineData("validator", "DailyEntry.Invalid:")]
    [InlineData("unknown grade", "DailyEntry.UnknownGrade:")]
    public async Task InvalidValues_AreRefusedByTheContract(string mistake, string code)
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(Roles.Manager);
        await using var client = await ConnectAsync(host, await IssueAsync(host, farm.User, WriteOnly));
        var arguments = mistake == "validator"
            ? With(Arguments(farm.FlockName), "totalEggs", -1)
            : Arguments(farm.FlockName, grades: [new { grade = "Jumbo", quantity = 90 }]);

        var refused = await CallAsync(client, arguments);

        Assert.Contains(code, ErrorText(refused));
        Assert.Equal(0, await EntryCountAsync(farm));
    }

    // Guard 19: the REST route and the tool keep separate claims, so one key used on both
    // runs both.
    [Fact]
    public async Task SameKey_OnTheRestRouteAndTheTool_DoNotAlias()
    {
        using var host = Host(factory);
        var farm = await SeedFarmAsync(Roles.Manager);
        var key = Guid.NewGuid().ToString();
        var rest = await factory.CreateAuthedClient(farm.User.Jwt).PostWithKeyAsync("/api/v1/daily-entries", key, new
        {
            farmId = farm.FarmId, houseId = farm.HouseId, flockId = farm.FlockId, date = Today.AddDays(-1),
            totalEggs = 100, crackedEggs = 2, dirtyEggs = 3, discardedEggs = 1, mortalityCount = 0,
        });
        Assert.Equal(HttpStatusCode.Created, rest.StatusCode);
        await using var client = await ConnectAsync(host, await IssueAsync(host, farm.User, WriteOnly));

        var reply = await RecordAsync(client, With(Arguments(farm.FlockName), "idempotencyKey", key));

        Assert.False(reply.GetProperty("replayed").GetBoolean());
        Assert.Equal(2, await EntryCountAsync(farm));
    }

    internal sealed record Farm(User User, Guid FarmId, Guid HouseId, Guid FlockId, string FlockName, Guid LargeGradeId);

    internal static async Task<Farm> SeedFarmAsync(CluckworkWebApplicationFactory factory, string? role)
    {
        var user = await SeedAsync(factory, role);
        var farmId = Guid.NewGuid();
        var houseId = Guid.NewGuid();
        var flockId = await factory.SeedFlockAsync(user.AccountId, farmId, houseId);
        var grades = await factory.SeedEggGradesAsync(user.AccountId, farmId, "Large");
        return new(user, farmId, houseId, flockId, $"Flock-{flockId.ToString()[..8]}", grades["Large"]);
    }

    private Task<Farm> SeedFarmAsync(string? role) => SeedFarmAsync(factory, role);

    internal static Dictionary<string, object?> Arguments(string flock, DateOnly? date = null, object[]? grades = null) => new()
    {
        ["idempotencyKey"] = Guid.NewGuid().ToString(),
        ["flock"] = flock,
        ["date"] = (date ?? Today).ToString("yyyy-MM-dd"),
        ["totalEggs"] = 100,
        ["crackedEggs"] = 2,
        ["dirtyEggs"] = 3,
        ["discardedEggs"] = 1,
        ["mortalityCount"] = 0,
        ["grades"] = grades,
    };

    internal static Dictionary<string, object?> With(Dictionary<string, object?> arguments, string name, object? value) =>
        new(arguments) { [name] = value };

    internal static async Task<CallToolResult> CallAsync(McpClient client, IReadOnlyDictionary<string, object?> arguments) =>
        await client.CallToolAsync(DailyEntryTools.RecordDailyEntry, arguments);

    internal static async Task<JsonElement> RecordAsync(McpClient client, IReadOnlyDictionary<string, object?> arguments)
    {
        var result = await CallAsync(client, arguments);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, text);
        return JsonDocument.Parse(text).RootElement;
    }

    internal static string ErrorText(CallToolResult result)
    {
        Assert.True(result.IsError);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    private static async Task<string> TextAsync(McpClient client, string tool) =>
        Assert.IsType<TextContentBlock>(Assert.Single((await client.CallToolAsync(tool)).Content)).Text;

    internal static Task<List<(string Action, string? App)>> AuditAsync(
        CluckworkWebApplicationFactory factory, Guid accountId, Guid entryId) =>
        factory.WithTenantScopeAsync(accountId, async db => (await db.AuditEvents
            .Where(a => a.EntityId == entryId)
            .OrderBy(a => a.OccurredAtUtc)
            .Select(a => new { a.Action, a.ConnectedAppClientId })
            .ToListAsync()).Select(a => (a.Action, a.ConnectedAppClientId)).ToList());

    private Task<List<(string Action, string? App)>> AuditAsync(Guid accountId, Guid entryId) =>
        AuditAsync(factory, accountId, entryId);

    internal static Task<int> EntryCountAsync(CluckworkWebApplicationFactory factory, Farm farm) =>
        factory.WithTenantScopeAsync(farm.User.AccountId, db => db.DailyEntries.CountAsync(e => e.FlockId == farm.FlockId));

    private Task<int> EntryCountAsync(Farm farm) => EntryCountAsync(factory, farm);

    private Task SetFlockAsync(Farm farm, FlockStatus status, DateOnly? depletedOn) =>
        factory.WithTenantScopeAsync(farm.User.AccountId, db => db.Flocks.Where(f => f.Id == farm.FlockId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, status).SetProperty(f => f.DepletedOn, depletedOn)));
}
