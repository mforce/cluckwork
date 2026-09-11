namespace Cluckwork.Application.Tests.Sales;

using System.Text.RegularExpressions;
using Cluckwork.Domain.Sales;

// #721 — DiscountReasonCode is mirrored by hand into web/src/i18n/enums.ts's
// DISCOUNT_REASON_VALUES, which is what the confirm dialog's picklist renders.
// Nothing else couples them: the SPA's own enums.test.ts checks that every
// listed value has an en.enums label, not that the list is the server's list,
// so a member added to the C# enum would simply never appear in the picklist
// and a member removed from it would render an option the server refuses.
//
// Same mechanism and the same reason as
// AuditVocabularyCoverageTests.AuditActions_registry_matches_the_SPA_AUDIT_ACTION_VALUES_list,
// which pins the two audit vocabularies. The parser is duplicated rather than
// shared: extracting it means editing that file's other guards, and this is the
// second use, not the second miss. A third mirrored enum is the point to extract.
public sealed class DiscountReasonVocabularyTests
{
    [Fact]
    public void DiscountReasonCode_matches_the_SPA_DISCOUNT_REASON_VALUES_list()
    {
        var server = Enum.GetNames<DiscountReasonCode>().ToHashSet();
        var client = ParseTsStringArray("DISCOUNT_REASON_VALUES");

        var missingFromClient = server.Except(client).OrderBy(x => x).ToList();
        var missingFromServer = client.Except(server).OrderBy(x => x).ToList();

        Assert.True(
            missingFromClient.Count == 0 && missingFromServer.Count == 0,
            "DiscountReasonCode vs web/src/i18n/enums.ts DISCOUNT_REASON_VALUES drifted."
            + (missingFromClient.Count > 0
                ? $"\nThe server accepts but the picklist is missing: {string.Join(", ", missingFromClient)}"
                : "")
            + (missingFromServer.Count > 0
                ? $"\nThe picklist offers but the server refuses: {string.Join(", ", missingFromServer)}"
                : ""));
    }

    // #721 — "Other" is the one member whose meaning the aggregate hard-codes
    // (it is the only code that makes the note mandatory), and SalesPage passes
    // its NAME to useConfirm's noteRequiredFor. Renaming the member without
    // renaming that literal would silently drop the inline note requirement.
    [Fact]
    public void Other_is_still_the_member_name_the_SPA_treats_as_needing_a_note()
    {
        Assert.Equal("Other", DiscountReasonCode.Other.ToString());
        Assert.Contains(
            "noteRequiredFor: [\"Other\" satisfies DiscountReasonValue]",
            File.ReadAllText(Path.Combine(RepositoryRoot(), "web", "src", "routes", "SalesPage.tsx")),
            StringComparison.Ordinal);
    }

    // web/src/i18n/enums.ts's `export const NAME = [...] as const;` array, read
    // as text: this is a C# test with no TypeScript runtime. Line comments are
    // stripped first, because commenting a value out (`// "Foo",`) is a natural
    // edit and must read as a removal.
    private static HashSet<string> ParseTsStringArray(string constantName)
    {
        var path = Path.Combine(RepositoryRoot(), "web", "src", "i18n", "enums.ts");
        var source = Regex.Replace(File.ReadAllText(path), @"//[^\n]*", string.Empty);

        var declaration = Regex.Match(
            source, $@"export const {constantName} = \[(.*?)\]\s*as const;", RegexOptions.Singleline);
        Assert.True(declaration.Success,
            $"Could not find 'export const {constantName} = [...] as const;' in {path}.");

        var values = Regex.Matches(declaration.Groups[1].Value, "\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
        Assert.NotEmpty(values);
        return values;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cluckwork.sln")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the Cluckwork repository root.");
    }
}
