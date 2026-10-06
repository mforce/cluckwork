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
        public Task<CredentialVerdict> VerifyAsync(
            Guid userId, Guid accountId, int tokenEpoch, CancellationToken ct = default) =>
            Task.FromResult(verdict);
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
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var problem = await JsonSerializer.DeserializeAsync<ProblemDetails>(
            context.Response.Body, JsonSerializerOptions.Web);
        Assert.Equal(expectedTitle, problem!.Title);
    }

    [Fact]
    public async Task Current_IsAdmitted()
    {
        var context = AuthenticatedRequest();
        var admitted = false;

        await new CredentialEpochMiddleware(_ => { admitted = true; return Task.CompletedTask; })
            .InvokeAsync(context, new FixedVerifier(CredentialVerdict.Current));

        Assert.True(admitted);
    }
}
