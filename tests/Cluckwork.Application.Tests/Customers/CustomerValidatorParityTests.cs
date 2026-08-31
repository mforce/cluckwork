namespace Cluckwork.Application.Tests.Customers;

using Cluckwork.Application.Features.Customers.CreateCustomer;
using Cluckwork.Application.Features.Customers.UpdateCustomer;
using Cluckwork.Domain.Sales;
using FluentValidation.Results;

// #625 review round 2/3 — Create and Update accept the same five customer
// fields, and the design's own invariant is that the update validator
// "mirrors every field" the create validator covers. This walks every
// SHARED boundary through BOTH real validators and asserts the two produce
// the EXACT SAME set of explicit (property, code) failures — not merely
// "each contains the one code under test" (round 2), which cannot see an
// extra or missing failure alongside it. It also proves the boundary is not
// off by one: a valid, AT-the-maximum value for each shared max-length field
// must pass both validators, so a unilateral tightening (one validator's
// literal quietly shortened, or hardcoded instead of referencing the
// Customer.Max*Length constant) fails on the validator that tightened.
// No production abstraction: both validators are exercised exactly as
// shipped.
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

    private static HashSet<(string PropertyName, string Code)> ExplicitCodePairs(ValidationResult result) =>
        result.Errors
            .Where(e => e.ErrorCode is not null && e.ErrorCode.Contains('.'))
            .Select(e => (e.PropertyName, e.ErrorCode!))
            .ToHashSet();

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
    public async Task SharedBoundary_ProducesTheExactSameErrorSet_OnBothValidators(
        string caseName,
        Func<CreateCustomerCommand, CreateCustomerCommand> mutateCreate,
        Func<UpdateCustomerCommand, UpdateCustomerCommand> mutateUpdate,
        string expectedCode)
    {
        var createResult = await _createValidator.ValidateAsync(mutateCreate(ValidCreate()));
        var updateResult = await _updateValidator.ValidateAsync(mutateUpdate(ValidUpdate()));

        var createPairs = ExplicitCodePairs(createResult);
        var updatePairs = ExplicitCodePairs(updateResult);

        var expectedProperty = expectedCode.Split('.')[1];
        Assert.Contains((expectedProperty, expectedCode), createPairs);
        // The EXACT set, not "update also contains it": a validator that adds
        // (or drops) an unrelated explicit failure for the same mutation
        // would still pass a Contains-only check.
        Assert.Equal(createPairs, updatePairs);
        _ = caseName; // xUnit theory display name only
    }

    public static IEnumerable<object[]> SharedMaxLengthAtExactBoundary()
    {
        var nameAtMax = new string('a', Customer.MaxNameLength);
        var phoneAtMax = new string('1', Customer.MaxPhoneLength);
        var addressAtMax = new string('a', Customer.MaxAddressLength);
        var noteAtMax = new string('a', Customer.MaxNoteLength);
        const string domain = "@example.com";
        var emailAtMax = new string('a', Customer.MaxEmailLength - domain.Length) + domain;

        yield return new object[]
        {
            "NameAtMax",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Name = nameAtMax }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Name = nameAtMax }),
        };
        yield return new object[]
        {
            "PhoneAtMax",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Phone = phoneAtMax }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Phone = phoneAtMax }),
        };
        yield return new object[]
        {
            "EmailAtMax",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Email = emailAtMax }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Email = emailAtMax }),
        };
        yield return new object[]
        {
            "AddressAtMax",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Address = addressAtMax }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Address = addressAtMax }),
        };
        yield return new object[]
        {
            "NoteAtMax",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Note = noteAtMax }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Note = noteAtMax }),
        };
    }

    [Theory]
    [MemberData(nameof(SharedMaxLengthAtExactBoundary))]
    public async Task SharedMaxLength_AtExactBoundary_PassesBothValidators(
        string caseName,
        Func<CreateCustomerCommand, CreateCustomerCommand> mutateCreate,
        Func<UpdateCustomerCommand, UpdateCustomerCommand> mutateUpdate)
    {
        var createResult = await _createValidator.ValidateAsync(mutateCreate(ValidCreate()));
        var updateResult = await _updateValidator.ValidateAsync(mutateUpdate(ValidUpdate()));

        Assert.True(createResult.IsValid,
            $"create: {string.Join(",", createResult.Errors.Select(e => $"{e.PropertyName}:{e.ErrorCode}"))}");
        Assert.True(updateResult.IsValid,
            $"update: {string.Join(",", updateResult.Errors.Select(e => $"{e.PropertyName}:{e.ErrorCode}"))}");
        _ = caseName;
    }

    [Fact]
    public async Task ValidCommand_PassesBothValidators()
    {
        Assert.True((await _createValidator.ValidateAsync(ValidCreate())).IsValid);
        Assert.True((await _updateValidator.ValidateAsync(ValidUpdate())).IsValid);
    }

    // Closure fix — Email/Address/Note are all optional, and each validator's
    // rules must treat null, "", and whitespace-only as the SAME accepted
    // "absent" input (Email's format/length rules are gated behind
    // `.When(x => !string.IsNullOrWhiteSpace(x.Email))`; Address/Note carry
    // no required rule at all, so MaximumLength alone must not reject an
    // empty/null value). Both real validators, exact failure-set equality —
    // same shape as the boundary theory above, just for the accepted side.
    public static IEnumerable<object[]> SharedOptionalFieldEmptyVariants()
    {
        yield return new object[]
        {
            "EmailNull",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Email = null }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Email = null }),
        };
        yield return new object[]
        {
            "EmailEmpty",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Email = "" }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Email = "" }),
        };
        yield return new object[]
        {
            "EmailWhitespaceOnly",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Email = "   " }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Email = "   " }),
        };
        yield return new object[]
        {
            "AddressNull",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Address = null }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Address = null }),
        };
        yield return new object[]
        {
            "AddressEmpty",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Address = "" }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Address = "" }),
        };
        yield return new object[]
        {
            "AddressWhitespaceOnly",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Address = "   " }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Address = "   " }),
        };
        yield return new object[]
        {
            "NoteNull",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Note = null }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Note = null }),
        };
        yield return new object[]
        {
            "NoteEmpty",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Note = "" }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Note = "" }),
        };
        yield return new object[]
        {
            "NoteWhitespaceOnly",
            (Func<CreateCustomerCommand, CreateCustomerCommand>)(c => c with { Note = "   " }),
            (Func<UpdateCustomerCommand, UpdateCustomerCommand>)(c => c with { Note = "   " }),
        };
    }

    [Theory]
    [MemberData(nameof(SharedOptionalFieldEmptyVariants))]
    public async Task SharedOptionalField_NullEmptyOrWhitespace_AcceptedIdenticallyByBothValidators(
        string caseName,
        Func<CreateCustomerCommand, CreateCustomerCommand> mutateCreate,
        Func<UpdateCustomerCommand, UpdateCustomerCommand> mutateUpdate)
    {
        var createResult = await _createValidator.ValidateAsync(mutateCreate(ValidCreate()));
        var updateResult = await _updateValidator.ValidateAsync(mutateUpdate(ValidUpdate()));

        Assert.True(createResult.IsValid,
            $"create: {string.Join(",", createResult.Errors.Select(e => $"{e.PropertyName}:{e.ErrorCode}"))}");
        Assert.True(updateResult.IsValid,
            $"update: {string.Join(",", updateResult.Errors.Select(e => $"{e.PropertyName}:{e.ErrorCode}"))}");
        // Both empty (IsValid already proved that) — the exact-set comparison
        // is what the round-3 boundary theory established as the real parity
        // proof; repeated here so this case can't silently start allowing a
        // stray explicit failure that IsValid alone (only false on FluentValidation's
        // own Severity.Error) would miss if a rule were ever downgraded to a warning.
        Assert.Equal(ExplicitCodePairs(createResult), ExplicitCodePairs(updateResult));
        _ = caseName;
    }
}
