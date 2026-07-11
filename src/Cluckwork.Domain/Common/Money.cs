namespace Cluckwork.Domain.Common;

// All money is stored as integer minor units (e.g. 1099 = $10.99 USD).
// Never use decimal/float for money; only convert to decimal for display.
public sealed record Money(long MinorUnits, string CurrencyCode, int CurrencyMinorUnit)
{
    public static Money Zero(string currencyCode, int minorUnit = 2) =>
        new(0, currencyCode, minorUnit);

    public Money Add(Money other)
    {
        AssertSameCurrency(other);
        return this with { MinorUnits = MinorUnits + other.MinorUnits };
    }

    public Money Subtract(Money other)
    {
        AssertSameCurrency(other);
        return this with { MinorUnits = MinorUnits - other.MinorUnits };
    }

    public Money Multiply(int factor) =>
        this with { MinorUnits = MinorUnits * factor };

    public bool IsNegative => MinorUnits < 0;

    public decimal ToDecimal() =>
        (decimal)MinorUnits / (decimal)Math.Pow(10, CurrencyMinorUnit);

    public override string ToString() =>
        $"{ToDecimal().ToString($"F{CurrencyMinorUnit}")} {CurrencyCode}";

    private void AssertSameCurrency(Money other)
    {
        if (CurrencyCode != other.CurrencyCode)
            throw new InvalidOperationException(
                $"Currency mismatch: {CurrencyCode} vs {other.CurrencyCode}.");
    }
}
