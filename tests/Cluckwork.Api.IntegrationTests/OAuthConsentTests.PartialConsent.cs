using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Api.Modules.Access.OAuth;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.IntegrationTests;

// #1326 — partial consent: the person grants the requested scopes they tick (G), never one
// the app did not ask for (R) or their role cannot use, and the token, the stored
// authorization and the audit row all hold exactly G.
public sealed partial class OAuthConsentTests
{
    private const string Submit = OAuthScopes.SubmitDailyEntries;
    private const string Expenses = OAuthScopes.WriteExpenses;
    private static readonly string[] All = [.. OAuthScopes.All];

    [Fact]
    public async Task TokenResponse_ScopesEqualTheSelection()
    {
        var (_, _, jwt) = await SeedAsync(Roles.Owner);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        var verifier = OAuthServerTests.NewCodeVerifier();

        var location = await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, All, verifier), [Read, Write]);
        using var redeemed = await RedeemAsync(clientId, QueryHelpers.ParseQuery(location.Query)["code"].ToString(), verifier);

        var body = await redeemed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        Assert.True(body.TryGetProperty("scope", out var scope), "the token response names no scope");
        Assert.Equal([Write, Read], scope.GetString()!.Split(' ').Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Selection_StoresExactlyTheChosenScopes()
    {
        var (accountId, userId, jwt) = await SeedAsync(Roles.Owner);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);

        await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, All), [Read, Write]);

        Assert.Equal([[Write, Read]], await AuthorizationScopesAsync(userId));
    }

    [Fact]
    public async Task Selection_AuditsTheChosenAndTheRequestedScopes()
    {
        var (accountId, userId, jwt) = await SeedAsync(Roles.Owner);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);

        await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, All), [Write]);

        var audit = Assert.Single(await ConsentAuditAsync(accountId, userId));
        Assert.Equal((AuditActions.UserAppConnected, Write), (audit.Action, audit.Scopes));
        Assert.Equal(string.Join(' ', All.Order(StringComparer.Ordinal)), audit.Requested);
    }

    // Unrequested, unknown or case-changed names are refused, never dropped, and before the
    // proof is spent: the same proof then approves a valid choice.
    [Theory]
    [InlineData(Submit)]
    [InlineData("cw1326.unknown")]
    [InlineData("FARM:READ")]
    public async Task UnrequestedScope_IsRefused_BeforeTheProofIsSpent(string unrequested)
    {
        var (accountId, userId, jwt) = await SeedAsync(Roles.Owner);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        var proof = await OAuthServerTests.StepUpAsync(factory, jwt);

        using var refused = await SendChoiceAsync(jwt, Query(clientId, Read, Write), proof, [Read, unrequested]);
        using var approved = await SendChoiceAsync(jwt, Query(clientId, Read, Write), proof, [Read]);

        await AssertInvalidChoiceAsync(refused);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal([[Read]], await AuthorizationScopesAsync(userId));
        Assert.Single(await ConsentAuditAsync(accountId, userId));
    }

    // The server decides what a role can use, from the tools' own policies: a Worker cannot
    // grant expenses:write even when the app asks for it and the request names it.
    [Fact]
    public async Task RoleBlockedScope_IsRefused_BeforeTheProofIsSpent()
    {
        var (accountId, userId, jwt) = await SeedAsync(role: null);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        var proof = await OAuthServerTests.StepUpAsync(factory, jwt);

        using var refused = await SendChoiceAsync(jwt, Query(clientId, Read, Expenses), proof, [Read, Expenses]);
        using var approved = await SendChoiceAsync(jwt, Query(clientId, Read, Expenses), proof, [Read]);

        await AssertInvalidChoiceAsync(refused);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal([[Read]], await AuthorizationScopesAsync(userId));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"farm:read\"")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"farm:read\"")]
    public async Task MalformedChoice_IsRefused_BeforeTheProofIsSpent(string header)
    {
        var (accountId, userId, jwt) = await SeedAsync(Roles.Owner);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        var proof = await OAuthServerTests.StepUpAsync(factory, jwt);

        using var refused = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, Query(clientId, Read), proof, consentScopes: header);
        using var approved = await SendChoiceAsync(jwt, Query(clientId, Read), proof, [Read]);

        await AssertInvalidChoiceAsync(refused);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Single(await AuthorizationScopesAsync(userId));
    }

    // Two header fields are never joined into one choice.
    [Fact]
    public async Task RepeatedChoiceHeader_IsRefused()
    {
        var (accountId, userId, jwt) = await SeedAsync(Roles.Owner);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        var request = new HttpRequestMessage(HttpMethod.Get,
            QueryHelpers.AddQueryString("/api/v1/oauth/authorize", Query(clientId, Read, Write)));
        request.Headers.Add(AuthEndpoints.StepUpHeaderName, await OAuthServerTests.StepUpAsync(factory, jwt));
        request.Headers.TryAddWithoutValidation(OAuthEndpoints.ConsentScopesHeaderName, ["[\"farm:read\"]", "[\"daily-entries:write\"]"]);

        using var refused = await OAuthServerTests.HttpsClient(factory, jwt).SendAsync(request);

        await AssertInvalidChoiceAsync(refused);
        Assert.Empty(await AuthorizationScopesAsync(userId));
    }

    [Fact]
    public async Task DuplicateChosenNames_AreGrantedOnce()
    {
        var (accountId, userId, jwt) = await SeedAsync(Roles.Owner);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);

        await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, All), [Write, Read, Write]);

        Assert.Equal([[Write, Read]], await AuthorizationScopesAsync(userId));
    }

    // Unticking everything is Cancel: access_denied to the app, nothing stored or audited,
    // and the proof stays unspent.
    [Fact]
    public async Task EmptyChoice_Cancels_WithoutSpendingTheProof()
    {
        var (accountId, userId, jwt) = await SeedAsync(Roles.Owner);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        var proof = await OAuthServerTests.StepUpAsync(factory, jwt);

        using var cancelled = await SendChoiceAsync(jwt, Query(clientId, Read, Write), proof, []);
        var parameters = QueryHelpers.ParseQuery((await RedirectFromAsync(cancelled, "an empty choice did not go back to the app")).Query);
        Assert.True(parameters.GetValueOrDefault("error") == Errors.AccessDenied, "an empty choice was not cancelled");
        Assert.Equal("state-795", parameters["state"].ToString());
        Assert.Empty(await AuthorizationScopesAsync(userId));
        Assert.Empty(await ConsentAuditAsync(accountId, userId));

        using var approved = await SendChoiceAsync(jwt, Query(clientId, Read, Write), proof, [Read]);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Single(await AuthorizationScopesAsync(userId));
    }

    // Like Cancel, an empty choice still sends the person back to the app when the farm has
    // turned connected apps off.
    [Fact]
    public async Task EmptyChoice_Cancels_WhenTheFarmSwitchIsOff()
    {
        var email = $"oauth-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var jwt = await factory.LoginForAccessTokenAsync(email);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        await factory.WithTenantScopeAsync(accountId, db => db.Accounts.Where(a => a.Id == accountId)
            .ExecuteUpdateAsync(set => set.SetProperty(a => a.AllowConnectedApps, false)));

        using var cancelled = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, Query(clientId, Read), consentScopes: "[]");

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var location = await RedirectFromAsync(cancelled, "an empty choice was refused instead of cancelled");
        Assert.Equal(Errors.AccessDenied, QueryHelpers.ParseQuery(location.Query)["error"].ToString());
    }

    // The default for a request without a scope is farm:read, and it can be unticked too.
    [Fact]
    public async Task DefaultReadScope_CanBeUnticked()
    {
        var (accountId, userId, jwt) = await SeedAsync(Roles.Owner);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);

        using var cancelled = await SendChoiceAsync(jwt, Query(clientId), await OAuthServerTests.StepUpAsync(factory, jwt), []);

        await RedirectFromAsync(cancelled, "unticking the default read scope did not cancel");
        Assert.Empty(await AuthorizationScopesAsync(userId));
    }

    // A choice without a password issues nothing: the consent payload comes back.
    [Fact]
    public async Task ChoiceWithoutAProof_IssuesNothing()
    {
        var (accountId, userId, jwt) = await SeedAsync(Roles.Owner);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, Read), [Read]);

        using var response = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, Query(clientId, Read), consentScopes: "[\"farm:read\"]");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("redirectUri", out _), "a choice without a password minted a code");
        Assert.Single(await ConsentAuditAsync(accountId, userId));
    }

    // An SPA older than #1326 sends no choice: it gets every requested scope the role can
    // use, and nothing at all when the role can use none of them.
    [Fact]
    public async Task ApprovalWithoutAChoice_GrantsTheRequestedScopesTheRoleCanUse()
    {
        var (accountId, userId, jwt) = await SeedAsync(role: null);
        var first = await OAuthServerTests.RegisterClientAsync(factory.Services);
        var second = await OAuthServerTests.RegisterClientAsync(factory.Services);

        await OAuthServerTests.ApproveAsync(factory, jwt, Query(first, Read, Write, Expenses));
        using var nothingUsable = await OAuthServerTests.SendAuthorizeAsync(
            factory, jwt, Query(second, Expenses), await OAuthServerTests.StepUpAsync(factory, jwt));

        Assert.Equal([[Write, Read]], await AuthorizationScopesAsync(userId));
        var location = await RedirectFromAsync(nothingUsable, "a request the role can use none of was not cancelled");
        Assert.Equal(Errors.AccessDenied, QueryHelpers.ParseQuery(location.Query)["error"].ToString());
    }

    // The payload names which requested scopes the role can use; the SPA shows the rest as
    // not allowed and has no role list of its own.
    [Fact]
    public async Task Payload_NamesTheUsableScopes_ForTheRole()
    {
        var (_, _, owner) = await SeedAsync(Roles.Owner);
        var (_, _, worker) = await SeedAsync(role: null);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);

        using var forOwner = await OAuthServerTests.SendAuthorizeAsync(factory, owner, Query(clientId, All));
        using var forWorker = await OAuthServerTests.SendAuthorizeAsync(factory, worker, Query(clientId, All));

        Assert.Equal(All.Order(), Strings(await forOwner.Content.ReadFromJsonAsync<JsonElement>(), "usable").Order());
        Assert.Equal(All.Where(scope => scope != Expenses).Order(),
            Strings(await forWorker.Content.ReadFromJsonAsync<JsonElement>(), "usable").Order());
    }

    [Fact]
    public async Task SameChoiceAgain_ReusesTheAuthorization_AndAuditsAReconnection()
    {
        var (accountId, userId, jwt) = await SeedAsync(Roles.Owner);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);

        await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, All), [Read, Write]);
        await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, All), [Write, Read]);

        Assert.Equal([[Write, Read]], await AuthorizationScopesAsync(userId));
        Assert.Equal([AuditActions.UserAppConnected, AuditActions.UserAppReconnected],
            (await ConsentAuditAsync(accountId, userId)).Select(audit => audit.Action));
    }

    // A narrower approval gets its own exact authorization and is a new connection; the
    // wider one stays until Disconnect.
    [Fact]
    public async Task NarrowerChoice_CreatesItsOwnAuthorization_AndKeepsTheWiderOne()
    {
        var (accountId, userId, jwt) = await SeedAsync(Roles.Owner);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);

        await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, Read, Write), [Read, Write]);
        await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, Read, Write), [Read]);

        Assert.Equal([[Read], [Write, Read]], (await AuthorizationScopesAsync(userId)).OrderBy(scopes => scopes.Length));
        Assert.Equal([AuditActions.UserAppConnected, AuditActions.UserAppConnected],
            (await ConsentAuditAsync(accountId, userId)).Select(audit => audit.Action));
    }

    // After a partial approval, a request for everything names what was allowed before; no
    // declined scope is remembered and nothing is approved without the password.
    [Fact]
    public async Task ReconnectAfterAPartialGrant_NamesWhatWasAllowedBefore()
    {
        var (_, _, jwt) = await SeedAsync(Roles.Owner);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, All), [Read, Write]);

        using var response = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, Query(clientId, All));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(All.Order(), Strings(body, "scopes").Order());
        Assert.Equal([Write, Read], Strings(body, "alreadyAllowed").Order(StringComparer.Ordinal));
        Assert.False(body.GetProperty("alreadyApproved").GetBoolean(), "a partial grant counted as approving everything");
    }

    // Two approvals that together hold the request are not one that does.
    [Fact]
    public async Task SeparateGrants_DoNotTogetherApproveTheRequest()
    {
        var (accountId, userId, jwt) = await SeedAsync(Roles.Owner);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, Read), [Read]);
        await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, Write), [Write]);

        using var response = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, Query(clientId, Read, Write));
        await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, Read, Write), [Read, Write]);

        Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("alreadyApproved").GetBoolean(),
            "two separate grants counted as one that covers the request");
        Assert.Equal(3, (await AuthorizationScopesAsync(userId)).Count);
    }

    [Fact]
    public async Task PartialApproval_SpendsTheProofOnce()
    {
        var (accountId, userId, jwt) = await SeedAsync(Roles.Owner);
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        var proof = await OAuthServerTests.StepUpAsync(factory, jwt);

        using var approved = await SendChoiceAsync(jwt, Query(clientId, Read, Write), proof, [Read]);
        using var replayed = await SendChoiceAsync(jwt, Query(clientId, Read, Write), proof, [Write]);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, replayed.StatusCode);
        Assert.Single(await AuthorizationScopesAsync(userId));
        Assert.Single(await ConsentAuditAsync(accountId, userId));
    }

    private Task<HttpResponseMessage> SendChoiceAsync(
        string jwt, Dictionary<string, string?> query, string proof, string[] chosen) =>
        OAuthServerTests.SendAuthorizeAsync(factory, jwt, query, proof, consentScopes: JsonSerializer.Serialize(chosen));

    private static async Task AssertInvalidChoiceAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("OAuth.InvalidConsentScopes",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    // A fresh farm; the Owner is the account's own first user.
    private async Task<(Guid AccountId, Guid UserId, string Jwt)> SeedAsync(string? role)
    {
        var owner = $"oauth-owner-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(owner);
        var email = owner;
        if (role != Roles.Owner)
        {
            email = $"oauth-role-{Guid.NewGuid():N}@test.local";
            await factory.SeedUserAsync(accountId, email, role);
        }
        var userId = await factory.WithTenantScopeAsync(accountId, db =>
            db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync());
        return (accountId, userId, await factory.LoginForAccessTokenAsync(email));
    }

    // Every authorization the user holds, each as its scopes in ordinal order.
    private async Task<List<string[]>> AuthorizationScopesAsync(Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var all = new List<string[]>();
        await foreach (var authorization in authorizations.FindBySubjectAsync(userId.ToString()))
            all.Add([.. (await authorizations.GetScopesAsync(authorization)).Order(StringComparer.Ordinal)]);
        return all;
    }

    private async Task<List<(string Action, string Scopes, string Requested)>> ConsentAuditAsync(Guid accountId, Guid userId)
    {
        var rows = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(e => e.EntityId == userId
                && (e.Action == AuditActions.UserAppConnected || e.Action == AuditActions.UserAppReconnected))
            .OrderBy(e => EF.Property<long>(e, "Sequence"))
            .Select(e => new { e.Action, e.DetailsJson })
            .ToListAsync());
        return [.. rows.Select(row =>
        {
            var details = JsonDocument.Parse(row.DetailsJson!).RootElement;
            string Joined(string name) => string.Join(' ', details.GetProperty(name).EnumerateArray()
                .Select(scope => scope.GetString()).Order(StringComparer.Ordinal));
            return (row.Action, Joined("scopes"), Joined("requested"));
        })];
    }
}
