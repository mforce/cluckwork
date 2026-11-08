using System.IdentityModel.Tokens.Jwt;
using Cluckwork.Api.Configuration;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Cluckwork.Api.Middleware;

// #1281 (#1274 decision 18) — every Owner must use two-factor. An Owner's grace
// starts at their first authenticated request and lasts OwnerGraceDays. From
// the deadline an unenrolled Owner reaches only change-password, step-up,
// enrolment, its status read and logout; everything else, connected-app tokens
// and the consent route included, is 403. Never a 401 challenge, which would
// loop an MCP client through re-authorisation.
//
// The inputs are the role claim, the row's two columns from the credential
// check, the days and the clock, so nothing can exempt one user. It sits after
// MustChangePasswordMiddleware, which lets change-password through, so the
// password step is never gated.
public sealed class OwnerTwoFactorGateMiddleware(
    RequestDelegate next,
    IOptions<OwnerGraceOptions> options,
    TimeProvider clock)
{
    // A start the clock has not reached yet is skew up to this much; beyond it
    // a future-dated value counts as past the deadline.
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    // Method and exact path; the trailing-slash form is accepted as routing accepts it.
    private static readonly HashSet<(string Method, string Path)> AllowedRequests =
    [
        (HttpMethods.Post, "/api/v1/auth/change-password"),
        (HttpMethods.Post, "/api/v1/auth/step-up"),
        (HttpMethods.Get, "/api/v1/auth/two-factor"),
        (HttpMethods.Post, "/api/v1/auth/two-factor/start"),
        (HttpMethods.Post, "/api/v1/auth/two-factor/confirm"),
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!CredentialEpochMiddleware.ChecksRequest(context) || !context.User.IsInRole(Roles.Owner))
        {
            await next(context);
            return;
        }

        var check = context.Features.Get<CredentialCheck?>()
            ?? throw new InvalidOperationException(
                "An Owner request reached the two-factor gate without a credential check; "
                + "CredentialEpochMiddleware must run first.");
        var now = clock.GetUtcNow();
        var start = check.SecondFactorRequiredSince ?? await StartGraceAsync(context, now);

        if (check.TwoFactorEnabled
            || (start <= now + ClockSkew && now < start.AddDays(options.Value.OwnerGraceDays))
            || IsAllowed(context.Request))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = EnrolmentErrorCodes.EnrolmentRequired,
            Detail = "Set up two-factor authentication to continue.",
            Status = StatusCodes.Status403Forbidden,
        }, options: null, contentType: "application/problem+json");
    }

    // The start is now, for this request, whether or not the write lands. A
    // failed write lets the request through and the next request tries again
    // (the maintainer, 2026-10-21: "Log an alert event"). Only the write fails
    // open, so the services are resolved and the claims parsed before it; the
    // services are resolved only for a request that finds no start.
    private static async Task<DateTimeOffset> StartGraceAsync(HttpContext context, DateTimeOffset now)
    {
        var graceStart = context.RequestServices.GetRequiredService<IOwnerGraceStart>();
        var events = context.RequestServices.GetRequiredService<AuthSecurityEventLogger>();
        // The credential check parsed both claims before it read the row.
        var accountId = Guid.Parse(context.User.FindFirst("account_id")!.Value);
        var userId = Guid.Parse(context.User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value);
        try
        {
            await graceStart.StartAsync(accountId, userId, now, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // No exception: a database error can quote the row, and the event carries no user id.
            events.OwnerGraceStartFailed();
        }

        return now;
    }

    private static bool IsAllowed(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        if (path.Length > 1 && path.EndsWith('/')) path = path[..^1];
        return AllowedRequests.Contains((request.Method.ToUpperInvariant(), path.ToLowerInvariant()));
    }
}
