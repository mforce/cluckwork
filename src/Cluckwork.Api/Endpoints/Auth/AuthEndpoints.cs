namespace Cluckwork.Api.Endpoints.Auth;

using Cluckwork.Api.RateLimiting;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users.ChangeOwnPassword;
using Cluckwork.Infrastructure.Identity;
using FluentValidation;
using Microsoft.Extensions.Options;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        // Strict limit — login is the password-spraying target (#143). On success
        // the refresh token is set as an HttpOnly cookie (#145); the body carries
        // only the access token.
        group.MapPost("/login", Login)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitingOptions.LoginPolicyName)
            .WithName("Login")
            .WithSummary("Exchange credentials for an access token; sets the refresh-token cookie.");

        // Looser limit — refresh guards a high-entropy token AND carries
        // legitimate automatic session traffic, so it must not share login's
        // budget (several users behind one NAT IP would starve it). The refresh
        // token comes from the cookie, never the body (#145).
        group.MapPost("/refresh", Refresh)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitingOptions.RefreshPolicyName)
            .WithName("RefreshToken")
            .WithSummary("Rotate the refresh-token cookie and return a fresh access token.");

        // Anonymous + cookie-authenticated (like refresh): logout is proven by the
        // HttpOnly refresh cookie plus the CSRF header, so it works even with an
        // expired access token and needs no Idempotency-Key (an authenticated
        // logout would resolve a tenant and the idempotency middleware would then
        // demand one). It must always be able to destroy the session (#145 review).
        group.MapPost("/logout", Logout)
            .AllowAnonymous()
            .WithName("Logout")
            .WithSummary("Revoke the refresh token and expire its cookie.");

        // #165 — self-service password change, for EVERY role (so it can't live in
        // the Owner-only users group). Lives here because the response rotates the
        // refresh cookie, which is path-scoped to /api/v1/auth. It verifies a
        // credential, so it carries the login rate limit: an attacker holding a
        // stolen access token must not be able to brute-force the current password
        // and take the account over.
        group.MapPost("/change-password", ChangePassword)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitingOptions.LoginPolicyName)
            .WithName("ChangeOwnPassword")
            .WithSummary("Change your own password; signs out other devices.");

        return group;
    }

    // In every environment but Development the browser reaches the app over HTTPS
    // (TLS terminates at the proxy), so the auth cookie must be Secure regardless
    // of the internal proxy→app hop scheme.
    private static bool CookieSecure(IWebHostEnvironment env) => !env.IsDevelopment();

    private static async Task<IResult> Login(
        LoginRequest request, IIdentityProvider identity, HttpResponse response,
        IOptions<JwtOptions> jwt, IWebHostEnvironment env, CancellationToken ct)
    {
        var result = await identity.LoginAsync(request.Email, request.Password, ct);
        if (!result.IsSuccess)
            return Results.Problem(result.Error.Description, statusCode: 401, title: result.Error.Code);

        AuthCookies.SetRefreshCookie(response, result.Value.RefreshToken, jwt.Value.RefreshTokenDays, CookieSecure(env));
        return Results.Ok(new AccessTokenResponse(result.Value.AccessToken, result.Value.AccessTokenExpiry));
    }

    private static async Task<IResult> Refresh(
        HttpRequest request, HttpResponse response, IIdentityProvider identity,
        IOptions<JwtOptions> jwt, IWebHostEnvironment env, CancellationToken ct)
    {
        // CSRF: SameSite=Strict already keeps the cookie off cross-site requests;
        // the custom header (which a cross-site simple request can't set) is the
        // belt-and-braces second check.
        if (!AuthCookies.HasCsrfHeader(request))
            return Results.Problem("Missing required header.", statusCode: 403, title: "Auth.CsrfHeaderRequired");

        var refreshToken = AuthCookies.ReadRefreshCookie(request);
        if (refreshToken is null)
            return Results.Problem("Not authenticated.", statusCode: 401, title: "Identity.InvalidRefreshToken");

        var result = await identity.RefreshAsync(refreshToken, ct);
        if (!result.IsSuccess)
        {
            // The cookie's token is invalid / already rotated / expired — expire
            // it so the browser stops presenting a dead token on every load.
            AuthCookies.ClearRefreshCookie(response, CookieSecure(env));
            return Results.Problem(result.Error.Description, statusCode: 401, title: result.Error.Code);
        }

        AuthCookies.SetRefreshCookie(response, result.Value.RefreshToken, jwt.Value.RefreshTokenDays, CookieSecure(env));
        return Results.Ok(new AccessTokenResponse(result.Value.AccessToken, result.Value.AccessTokenExpiry));
    }

    private static async Task<IResult> ChangePassword(
        ChangeOwnPasswordRequest request, ChangeOwnPasswordHandler handler,
        IValidator<ChangeOwnPasswordCommand> validator, ICurrentUser currentUser,
        HttpResponse response, IOptions<JwtOptions> jwt, IWebHostEnvironment env,
        CancellationToken ct)
    {
        if (!currentUser.IsResolved) return Results.Unauthorized();

        var command = new ChangeOwnPasswordCommand(request.CurrentPassword, request.NewPassword);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        // The user id comes from the token, never the request: a caller can only
        // ever change their OWN password here.
        var result = await handler.HandleAsync(command, currentUser.UserId, ct);
        if (!result.IsSuccess)
            return Results.Problem(result.Error.Description, statusCode: 400, title: result.Error.Code);

        // Every prior session was revoked; hand this device a fresh cookie + access
        // token so the one that made the change stays signed in.
        AuthCookies.SetRefreshCookie(response, result.Value.RefreshToken, jwt.Value.RefreshTokenDays, CookieSecure(env));
        return Results.Ok(new AccessTokenResponse(result.Value.AccessToken, result.Value.AccessTokenExpiry));
    }

    private static async Task<IResult> Logout(
        HttpRequest request, HttpResponse response, IIdentityProvider identity,
        IWebHostEnvironment env, CancellationToken ct)
    {
        if (!AuthCookies.HasCsrfHeader(request))
            return Results.Problem("Missing required header.", statusCode: 403, title: "Auth.CsrfHeaderRequired");

        var refreshToken = AuthCookies.ReadRefreshCookie(request);
        if (refreshToken is not null)
            await identity.RevokeRefreshTokenAsync(refreshToken, ct);
        AuthCookies.ClearRefreshCookie(response, CookieSecure(env));
        return Results.NoContent();
    }
}

public sealed record LoginRequest(string Email, string Password);

public sealed record ChangeOwnPasswordRequest(string CurrentPassword, string NewPassword);

// #145 — the refresh token is delivered as a cookie, so the body returns only
// the access token and its expiry.
public sealed record AccessTokenResponse(string AccessToken, DateTimeOffset AccessTokenExpiry);
