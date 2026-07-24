namespace Cluckwork.Application.Features.Users;

// #163 — one place for the display-name rule shared by create and update: trim
// surrounding whitespace, and treat blank as "no name" (null) so a cleared field
// shows as "—" rather than storing an empty/whitespace string.
public static class UserName
{
    public const int MaxLength = 128;

    public static string? Normalize(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
