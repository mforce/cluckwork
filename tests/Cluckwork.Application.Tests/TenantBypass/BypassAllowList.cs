namespace Cluckwork.Application.Tests.TenantBypass;

// The one tenant-bypass allow-list every real-tree test reads (#859). Nothing else loads the real rows.
internal static class BypassAllowList
{
    internal static IReadOnlyList<AllowListEntry> Entries { get; } =
        AllowList.Load(Path.Combine(AppContext.BaseDirectory, "TenantBypass", "Data", "tenant-bypass-allowlist.json"));
}
