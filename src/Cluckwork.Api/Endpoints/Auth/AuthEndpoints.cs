namespace Cluckwork.Api.Endpoints.Auth;

using Cluckwork.Application.Common;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/login", Login)
            .AllowAnonymous()
            .WithName("Login")
            .WithSummary("Exchange user credentials for an asymmetric JWT access token.");

        group.MapPost("/refresh", Refresh)
            .AllowAnonymous()
            .WithName("RefreshToken")
            .WithSummary("Refresh an access token using a durable refresh token.");

        group.MapPost("/logout", Logout)
            .RequireAuthorization()
            .WithName("Logout")
            .WithSummary("Revoke a refresh token.");

        return group;
    }

    private static async Task<IResult> Login(LoginRequest request, IIdentityProvider identity, CancellationToken ct)
    {
        var result = await identity.LoginAsync(request.Email, request.Password, ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error.Description, statusCode: 401, title: result.Error.Code);
    }

    private static async Task<IResult> Refresh(RefreshTokenRequest request, IIdentityProvider identity, CancellationToken ct)
    {
        var result = await identity.RefreshAsync(request.RefreshToken, ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error.Description, statusCode: 401, title: result.Error.Code);
    }

    private static async Task<IResult> Logout(RefreshTokenRequest request, IIdentityProvider identity, CancellationToken ct)
    {
        await identity.RevokeRefreshTokenAsync(request.RefreshToken, ct);
        return Results.NoContent();
    }
}

public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshTokenRequest(string RefreshToken);
