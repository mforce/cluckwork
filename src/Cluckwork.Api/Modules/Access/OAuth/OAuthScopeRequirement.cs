using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;

namespace Cluckwork.Api.Modules.Access.OAuth;

// #806 — the caller's token must hold one of these scopes. A requirement type, not an
// assertion, so a denial can name the scopes that would have passed (insufficient_scope,
// ForbiddenProblemResultHandler). It is its own handler, as AssertionRequirement is.
public sealed class OAuthScopeRequirement(params string[] anyOf) : IAuthorizationRequirement, IAuthorizationHandler
{
    public IReadOnlyList<string> AnyOf { get; } = anyOf;

    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        if (AnyOf.Any(context.User.HasScope))
            context.Succeed(this);
        return Task.CompletedTask;
    }
}
