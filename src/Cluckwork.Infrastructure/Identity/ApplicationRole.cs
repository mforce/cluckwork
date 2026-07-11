namespace Cluckwork.Infrastructure.Identity;

using Microsoft.AspNetCore.Identity;

public sealed class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() { }
    public ApplicationRole(string roleName) : base(roleName) { }
}
