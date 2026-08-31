namespace Cluckwork.Application.Tests.Customers;

using Cluckwork.Application.Features.Customers.UpdateCustomer;
using Cluckwork.Domain.Sales;

public sealed class UpdateCustomerValidatorTests
{
    private readonly UpdateCustomerValidator _validator = new();

    private static UpdateCustomerCommand Valid() => new(
        CustomerId: Guid.NewGuid(), Version: 0, Name: "Jane Farmer", Phone: "555-0100",
        Email: "jane@example.com", Address: "123 Coop Rd", Note: "Regular buyer");

    [Fact]
    public async Task ValidCommand_Passes()
    {
        Assert.True((await _validator.ValidateAsync(Valid())).IsValid);
    }

    [Fact]
    public async Task EmptyCustomerId_Fails()
    {
        var result = await _validator.ValidateAsync(Valid() with { CustomerId = Guid.Empty });
        Assert.Contains(result.Errors, e => e.ErrorCode == "Customer.CustomerId.Required");
    }

    [Fact]
    public async Task NegativeVersion_Fails()
    {
        var result = await _validator.ValidateAsync(Valid() with { Version = -1 });
        Assert.Contains(result.Errors, e => e.ErrorCode == "Customer.Version.NonNegative");
    }

    [Fact]
    public async Task BlankName_Fails()
    {
        var result = await _validator.ValidateAsync(Valid() with { Name = "   " });
        Assert.Contains(result.Errors, e => e.ErrorCode == "Customer.Name.Required");
    }

    [Fact]
    public async Task BlankPhone_Fails()
    {
        var result = await _validator.ValidateAsync(Valid() with { Phone = "   " });
        Assert.Contains(result.Errors, e => e.ErrorCode == "Customer.Phone.Required");
    }

    [Fact]
    public async Task NameTooLong_Fails()
    {
        var result = await _validator.ValidateAsync(Valid() with { Name = new string('a', Customer.MaxNameLength + 1) });
        Assert.Contains(result.Errors, e => e.ErrorCode == "Customer.Name.MaxLength");
    }

    [Fact]
    public async Task PhoneTooLong_Fails()
    {
        var result = await _validator.ValidateAsync(Valid() with { Phone = new string('1', Customer.MaxPhoneLength + 1) });
        Assert.Contains(result.Errors, e => e.ErrorCode == "Customer.Phone.MaxLength");
    }

    [Fact]
    public async Task EmailTooLong_Fails()
    {
        var longLocal = new string('a', Customer.MaxEmailLength);
        var result = await _validator.ValidateAsync(Valid() with { Email = $"{longLocal}@example.com" });
        Assert.Contains(result.Errors, e => e.ErrorCode == "Customer.Email.MaxLength");
    }

    [Fact]
    public async Task AddressTooLong_Fails()
    {
        var result = await _validator.ValidateAsync(Valid() with { Address = new string('a', Customer.MaxAddressLength + 1) });
        Assert.Contains(result.Errors, e => e.ErrorCode == "Customer.Address.MaxLength");
    }

    [Fact]
    public async Task NoteTooLong_Fails()
    {
        var result = await _validator.ValidateAsync(Valid() with { Note = new string('a', Customer.MaxNoteLength + 1) });
        Assert.Contains(result.Errors, e => e.ErrorCode == "Customer.Note.MaxLength");
    }

    [Fact]
    public async Task InvalidEmailFormat_Fails()
    {
        var result = await _validator.ValidateAsync(Valid() with { Email = "not-an-email" });
        Assert.Contains(result.Errors, e => e.ErrorCode == "Customer.Email.Format");
    }
}
