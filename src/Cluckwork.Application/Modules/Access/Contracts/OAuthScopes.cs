namespace Cluckwork.Application.Modules.Access.Contracts;

// #788 — the scopes a connected app can ask for. A scope only subtracts from the
// user's role (#796); #806 maps the tools to them. Each write scope is one consent
// permission (#1311, #1313).
public static class OAuthScopes
{
    public const string ReadFarm = "farm:read";
    public const string WriteDailyEntries = "daily-entries:write";
    public const string SubmitDailyEntries = "daily-entries:submit";
    public const string WriteOperations = "operations:write";
    public const string WriteExpenses = "expenses:write";

    public static IReadOnlyList<string> All { get; } =
        [ReadFarm, WriteDailyEntries, SubmitDailyEntries, WriteOperations, WriteExpenses];
}
