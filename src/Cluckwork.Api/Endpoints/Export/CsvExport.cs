namespace Cluckwork.Api.Endpoints.Export;

using System.Globalization;
using System.Text;
using Cluckwork.Application.Features.Export;

// #95 — RFC 4180 CSV: CRLF rows, quote-doubling, UTF-8 with BOM (Excel needs
// the BOM to detect the encoding). Rows stream straight from the source
// enumerable to the output — an export never materializes in memory.
public static class CsvExport
{
    /// <summary>Writes the dataset as CSV and returns the row count.</summary>
    public static async Task<int> WriteAsync(ExportDataset dataset, Stream output, CancellationToken ct)
    {
        await output.WriteAsync(Encoding.UTF8.GetPreamble(), ct);
        var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteAsync(Line(dataset.Header));
            var count = 0;
            await foreach (var row in dataset.Rows.WithCancellation(ct))
            {
                await writer.WriteAsync(Line(row.Select(FormatCell)));
                count++;
            }
            await writer.FlushAsync(ct);
            return count;
        }
    }

    private static string Line(IEnumerable<string> cells)
        => string.Join(',', cells.Select(Escape)) + "\r\n";

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
