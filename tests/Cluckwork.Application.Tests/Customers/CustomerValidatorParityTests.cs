namespace Cluckwork.Application.Tests.Customers;

using Cluckwork.Application.Features.Customers.CreateCustomer;
using Cluckwork.Application.Features.Customers.UpdateCustomer;
using Cluckwork.Domain.Sales;
using FluentValidation.Results;

// #625 review round 2 — Create and Update accept the same five customer
// fields, and the design's own invariant is that the update validator
// "mirrors every field" the create validator covers. This walks every
// SHARED boundary through BOTH real validators and asserts each produces the
// SAME explicit error code — a drift (one validator's rule loosened, or
// added to only one of the two) fails on the mismatch itself, rather than on
// trusting two hand-authored literal lists to stay in sync. No production
// abstraction: both validators are exercised exactly as shipped.
public sealed class CustomerValidatorParityTests
{
    private readonly CreateCustomerValidator _createValidator = new();
    private readonly UpdateCustomerValidator _updateValidator = new();

    private static CreateCustomerCommand ValidCreate() => new(
        Name: "Jane Farmer", Phone: "555-0100", Email: "jane@example.com",
        Address: "123 Coop Rd", Note: "Regular buyer");

    private static UpdateCustomerCommand ValidUpdate() => new(
        CustomerId: Guid.NewGuid(), Version: 0, Name: "Jane Farmer", Phone: "555-0100",
        Email: "jane@example.com", Address: "123 Coop Rd", Note: "Regular buyer");

    private static HashSet<string> ExplicitCodes(ValidationResult result) =>
        result.Errors
            .Select(e => e.ErrorCode)
            .Where(code => code is not null && code.Contains('.'))
            .ToHashSet()!;

    public static IEnumerable<object[]> SharedFieldBoundaries()
    {
        var longName = new string('a', Customer.MaxNameLength + 1);
        var longPhone = new string('1', Customer.MaxPhoneLength + 1);
        var longEmailLocal = new string('a', Customer.MaxEmailLength);
        var longAddress = new string('a', Customer.MaxAddressLength + 1);
        var longNote = new string('a', Customer.MaxNoteLength + 1);

        yield return new object[]
        {
            "BlankName",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Name = "   " }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Name = "   " }),
            "Customer.Name.Required",
        };
        yield return new object[]
        {
            "NameTooLong",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Name = longName }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Name = longName }),
            "Customer.Name.MaxLength",
        };
        yield return new object[]
        {
            "BlankPhone",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Phone = "   " }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Phone = "   " }),
            "Customer.Phone.Required",
        };
        yield return new object[]
        {
            "PhoneTooLong",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Phone = longPhone }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Phone = longPhone }),
            "Customer.Phone.MaxLength",
        };
        yield return new object[]
        {
            "InvalidEmailFormat",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Email = "not-an-email" }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Email = "not-an-email" }),
            "Customer.Email.Format",
        };
        yield return new object[]
        {
            "EmailTooLong",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Email = $"{longEmailLocal}@example.com" }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Email = $"{longEmailLocal}@example.com" }),
            "Customer.Email.MaxLength",
        };
        yield return new object[]
        {
            "AddressTooLong",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Address = longAddress }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Address = longAddress }),
            "Customer.Address.MaxLength",
        };
        yield return new object[]
        {
            "NoteTooLong",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Note = longNote }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Note = longNote }),
            "Customer.Note.MaxLength",
        };
    }

    [Theory]
    [MemberData(nameof(SharedFieldBoundaries))]
    public async Task SharedBoundary_ProducesTheSameExplicitCode_OnBothValidators(
        string caseName,
        Func<CreateCustomerCommand, CreateCustomerCommand> mutateCreate,
        Func<UpdateCustomerCommand, UpdateCustomerCommand> mutateUpdate,
        string expectedCode)
    {
        var createResult = await _createValidator.ValidateAsync(mutateCreate(ValidCreate()));
        var updateResult = await _updateValidator.ValidateAsync(mutateUpdate(ValidUpdate()));

        Assert.Contains(expectedCode, ExplicitCodes(createResult));
        Assert.Contains(expectedCode, ExplicitCodes(updateResult));
        _ = caseName; // xUnit theory display name only
    }

    [Fact]
    public async Task ValidCommand_PassesBothValidators()
    {
        Assert.True((await _createValidator.ValidateAsync(ValidCreate())).IsValid);
        Assert.True((await _updateValidator.ValidateAsync(ValidUpdate())).IsValid);
    }
}
