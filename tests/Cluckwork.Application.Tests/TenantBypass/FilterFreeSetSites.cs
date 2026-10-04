namespace Cluckwork.Application.Tests.TenantBypass;

// One classified filter-free-set site (#632). The key joins the identity fields exactly as written; Reason is
// stored as written and trimmed where it is read.
internal sealed record FilterFreeSetSite(string Symbol, string Set, string Signature, string Reason)
{
    internal string Key => string.Join("\t", Symbol, Set, Signature);
}

// The one filter-free-set classification list every real-tree test reads (#859).
internal static class FilterFreeSetSites
{
    // symbol <TAB> db.<Set> <TAB> signature <TAB> reason. A row with fewer than four fields reads with blank
    // fields, so the real-tree test reports it as malformed.
    internal static IReadOnlyList<FilterFreeSetSite> All { get; } =
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TenantBypass", "Data", "filter-free-set-sites.tsv"))
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"))
            .Select(l => l.Split('\t'))
            .Select(p => new FilterFreeSetSite(Field(p, 0), Field(p, 1), Field(p, 2), Field(p, 3)))
            .ToList();

    private static string Field(string[] parts, int index) => index < parts.Length ? parts[index] : string.Empty;
}
