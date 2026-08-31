namespace Cluckwork.Domain.Sales;

using Cluckwork.Domain.Common;

// MVP customer (issue #10): reference-app shape — name + phone required,
// email/address/note optional. Balances, credit terms, and payments are
// Phase 1.1.
public sealed class Customer : AggregateRoot<Guid>
{
    public const int MaxNameLength = 200;
    public const int MaxPhoneLength = 50;
    public const int MaxEmailLength = 254;
    public const int MaxAddressLength = 500;
    public const int MaxNoteLength = 1000;

    public string Name { get; private set; } = string.Empty;
    public string Phone { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public string? Address { get; private set; }
    public string? Note { get; private set; }
    public int Version { get; private set; }

    private Customer() { }

    public static Customer Create(
        Guid id, Guid accountId, string name, string phone,
        string? email = null, string? address = null, string? note = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Customer name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(phone))
            throw new ArgumentException("Customer phone is required.", nameof(phone));

        return new Customer
        {
            Id = id, AccountId = accountId,
            Name = name.Trim(),
            Phone = phone.Trim(),
            Email = Normalize(email),
            Address = Normalize(address),
            Note = Normalize(note)
        };
    }

    public Result Update(string name, string phone, string? email, string? address, string? note)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(Error.Validation("Customer.Name.Required", "Customer name is required."));
        if (string.IsNullOrWhiteSpace(phone))
            return Result.Failure(Error.Validation("Customer.Phone.Required", "Customer phone is required."));

        Name = name.Trim();
        Phone = phone.Trim();
        Email = Normalize(email);
        Address = Normalize(address);
        Note = Normalize(note);
        Version++;
        return Result.Success();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
