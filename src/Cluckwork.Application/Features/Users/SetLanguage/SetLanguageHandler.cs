namespace Cluckwork.Application.Features.Users.SetLanguage;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;

// The user id comes from the token (the endpoint), never the body: a caller can
// only ever set their OWN language. Account-scoped inside the provider.
public sealed class SetLanguageHandler(IIdentityProvider identity)
{
    public Task<Result> HandleAsync(
        SetLanguageCommand command, Guid accountId, Guid userId, CancellationToken ct = default)
        => identity.SetLanguageAsync(accountId, userId, command.Language, ct);
}
