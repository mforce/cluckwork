namespace Cluckwork.Domain.Accounts;

public sealed class Account : AggregateRoot<Guid>
{
    public string Name { get; private set; } = string.Empty;
    public string TimeZoneId { get; private set; } = "UTC";
    public string DefaultCurrencyCode { get; private set; } = "USD";
    public int DefaultCurrencyMinorUnit { get; private set; } = 2;
    public bool IsActive { get; private set; }

    private Account() { }

    public static Account Create(Guid id, string name, string timeZoneId, string currencyCode)
    {
        return new Account
        {
            Id = id,
            AccountId = id,
            Name = name,
            TimeZoneId = timeZoneId,
            DefaultCurrencyCode = currencyCode,
            IsActive = true
        };
    }
}
