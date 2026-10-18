using System.Globalization;
using Cluckwork.Domain.Common;
using ModelContextProtocol;

namespace Cluckwork.Api.Mcp;

// #808, guard row 28 — an amount as a model reads it: a decimal string in the currency's
// own precision. Minor units never cross, because "2825000" with "minorUnit": 2 invites a
// 100× misreading of a figure a farmer acts on.
public sealed record McpMoney(string Amount, string Currency)
{
    public static McpMoney From(Money money) =>
        new(money.ToDecimal().ToString($"F{money.CurrencyMinorUnit}", CultureInfo.InvariantCulture), money.CurrencyCode);

    public static McpMoney From(long minorUnits, string currencyCode, int currencyMinorUnit) =>
        From(new Money(minorUnits, currencyCode, currencyMinorUnit));

    // The reverse, for an amount a model sends: refused, never rounded, past the currency's precision.
    public static long ToMinorUnits(decimal amount, int currencyMinorUnit, string parameter)
    {
        try
        {
            var scaled = amount * (decimal)Math.Pow(10, currencyMinorUnit);
            if (scaled == decimal.Truncate(scaled))
                return checked((long)scaled);
        }
        catch (OverflowException)
        {
            throw new McpException($"{parameter} is too large.");
        }

        throw new McpException($"{parameter} has more decimal places than the farm's currency allows ({currencyMinorUnit}).");
    }
}
