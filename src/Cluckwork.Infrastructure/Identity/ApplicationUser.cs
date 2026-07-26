namespace Cluckwork.Infrastructure.Identity;

using Microsoft.AspNetCore.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public Guid AccountId { get; set; }
    public string? DisplayName { get; set; }

    // #45 — the user's UI-language preference, a nullable BCP-47 primary subtag
    // (lowercased). NOT a locale: regional/number/date formatting stays a
    // farm-scoped `Account` concern (§4.5). null = follow the app default.
    public string? Language { get; set; }
}
