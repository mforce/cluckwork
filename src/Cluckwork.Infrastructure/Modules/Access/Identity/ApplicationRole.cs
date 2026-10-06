using Microsoft.AspNetCore.Identity;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

public sealed class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() { }
    public ApplicationRole(string roleName) : base(roleName) { }
}
