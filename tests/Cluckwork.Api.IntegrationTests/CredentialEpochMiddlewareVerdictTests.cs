using Cluckwork.Application.Modules.Access.Contracts;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Cluckwork.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cluckwork.Api.IntegrationTests;

// #857 — the middleware admits a request on CredentialVerdict.Current and on
// nothing else. default(CredentialVerdict) and an undefined value are refused
// like any other non-Current verdict, so a verifier that returns default fails
// closed. No host or database: the verdict is the only input that varies.
public sealed class CredentialEpochMiddlewareVerdictTests
{
    private sealed class FixedVerifier(CredentialVerdict verdict) : ICredentialEpochVerifier
    {
        public static readonly DateTimeOffset RequiredSince = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        public Task<CredentialCheck> VerifyAsync(
            Guid userId, Guid accountId, int tokenEpoch, bool connectedApp, CancellationToken ct = default) =>
            Task.FromResult(new CredentialCheck(verdict, TwoFactorEnabled: true, RequiredSince));
    }

    private static DefaultHttpContext AuthenticatedRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/users";
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new Claim("account_id", Guid.NewGuid().ToString()),
            new Claim("credential_epoch", "1"),
        ], authenticationType: "Bearer"));
        return context;
    }

    [Theory]
    [InlineData(CredentialVerdict.UnknownUser, "Auth.CredentialsSuperseded")]
    [InlineData(CredentialVerdict.Disabled, "Auth.AccountDisabled")]
    [InlineData(CredentialVerdict.FarmSuspended, "Auth.FarmSuspended")]
    [InlineData(CredentialVerdict.ConnectedAppsOff, "Auth.ConnectedAppsOff")]
    [InlineData(CredentialVerdict.Superseded, "Auth.CredentialsSuperseded")]
    [InlineData((CredentialVerdict)0, "Auth.CredentialsSuperseded")]
    [InlineData((CredentialVerdict)99, "Auth.CredentialsSuperseded")]
    public async Task EveryVerdictButCurrent_IsRefused(CredentialVerdict verdict, string expectedTitle)
    {
        var context = AuthenticatedRequest();
        var admitted = false;

        await new CredentialEpochMiddleware(_ => { admitted = true; return Task.CompletedTask; })
            .InvokeAsync(context, new FixedVerifier(verdict));

        Assert.False(admitted);
        Assert.Null(context.Features.Get<CredentialCheck?>());
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var problem = await JsonSerializer.DeserializeAsync<ProblemDetails>(
            context.Response.Body, JsonSerializerOptions.Web);
        Assert.Equal(expectedTitle, problem!.Title);
    }

    // #1281 — the Owner two-factor gate reads what the check read, from the
    // request, on Current only.
    [Fact]
    public async Task Current_IsAdmitted_WithTheCheckAsARequestFeature()
    {
        var context = AuthenticatedRequest();
        CredentialCheck? seen = null;

        await new CredentialEpochMiddleware(next => { seen = next.Features.Get<CredentialCheck?>(); return Task.CompletedTask; })
            .InvokeAsync(context, new FixedVerifier(CredentialVerdict.Current));

        Assert.Equal(new CredentialCheck(CredentialVerdict.Current, true, FixedVerifier.RequiredSince), seen);
    }
}
