namespace Cluckwork.Infrastructure.Identity;

using Microsoft.AspNetCore.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public Guid AccountId { get; set; }
    public string? DisplayName { get; set; }
}
