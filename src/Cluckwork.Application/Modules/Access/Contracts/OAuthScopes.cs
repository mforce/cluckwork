namespace Cluckwork.Application.Modules.Access.Contracts;

// #788 — the two scopes a connected app can ask for. A scope only subtracts from the
// user's role (#796); #806 maps the tools to them.
public static class OAuthScopes
{
    public const string ReadFarm = "farm:read";
    public const string WriteDailyEntries = "daily-entries:write";

    public static IReadOnlyList<string> All { get; } = [ReadFarm, WriteDailyEntries];
}
