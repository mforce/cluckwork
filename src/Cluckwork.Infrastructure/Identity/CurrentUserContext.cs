namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Application.Common;

// Scoped per request, resolved by TenantResolutionMiddleware from the JWT's
// sub + email claims (TenantContext pattern).
public sealed class CurrentUserContext : ICurrentUser
{
    public bool IsResolved { get; private set; }
    public Guid UserId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public IReadOnlyList<string> Roles { get; private set; } = [];

    public void Resolve(Guid userId, string email, IReadOnlyList<string>? roles = null)
    {
        UserId = userId;
        Email = email;
        Roles = roles ?? [];
        IsResolved = true;
    }
}
