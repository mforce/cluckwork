namespace Cluckwork.Api.Endpoints.Export;

using System.Globalization;
using System.Text;
using Cluckwork.Application.Features.Export;

// #95 — RFC 4180 CSV: CRLF rows, quote-doubling, UTF-8 with BOM (Excel needs
// the BOM to detect the encoding).
public static class CsvExport
{
    public static byte[] ToBytes(ExportTable table)
    {
        var sb = new StringBuilder();
        AppendRow(sb, table.Header);
        foreach (var row in table.Rows)
            AppendRow(sb, row.Select(FormatCell));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static void AppendRow(StringBuilder sb, IEnumerable<string> cells)
        => sb.AppendJoin(',', cells.Select(Escape)).Append("\r\n");

    private static string FormatCell(object? value) => value switch
    {
        null => "",
        // Only strings carry user input; the formula guard must not touch our
        // own serializations (a negative amount is data, not an attack).
        string s => GuardFormula(s),
        bool b => b ? "true" : "false",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    // Spreadsheet apps execute cells starting with = + - @ as formulas — a
    // classic exfiltration vector via user-typed names/notes. A leading
    // apostrophe makes them render as literal text.
    private static string GuardFormula(string s)
        => s.Length > 0 && s[0] is '=' or '+' or '-' or '@' ? "'" + s : s;

    private static string Escape(string cell)
        => cell.Contains('"') || cell.Contains(',') || cell.Contains('\r') || cell.Contains('\n')
            ? "\"" + cell.Replace("\"", "\"\"") + "\""
            : cell;
}
