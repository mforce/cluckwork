namespace Cluckwork.Application.Features.Customers.UpdateCustomer;

using Cluckwork.Domain.Sales;
using FluentValidation;

public sealed class UpdateCustomerValidator : AbstractValidator<UpdateCustomerCommand>
{
    public UpdateCustomerValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty().WithErrorCode("Customer.CustomerId.Required");
        // An omitted Version binds to null (never the framework default 0),
        // so a caller who never actually loaded the row cannot silently pass
        // as if it had — see #625 review round 5 (CodeRabbit CR-1).
        RuleFor(x => x.Version).NotNull().WithErrorCode("Customer.Version.Required")
            .GreaterThanOrEqualTo(0).WithErrorCode("Customer.Version.NonNegative");
        RuleFor(x => x.Name).Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Name is required.")
            .WithErrorCode("Customer.Name.Required").MaximumLength(Customer.MaxNameLength)
            .WithErrorCode("Customer.Name.MaxLength");
        RuleFor(x => x.Phone).Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Phone is required.")
            .WithErrorCode("Customer.Phone.Required").MaximumLength(Customer.MaxPhoneLength)
            .WithErrorCode("Customer.Phone.MaxLength");
        RuleFor(x => x.Email).EmailAddress().WithErrorCode("Customer.Email.Format")
            .MaximumLength(Customer.MaxEmailLength).WithErrorCode("Customer.Email.MaxLength")
            .When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Address).MaximumLength(Customer.MaxAddressLength).WithErrorCode("Customer.Address.MaxLength");
        RuleFor(x => x.Note).MaximumLength(Customer.MaxNoteLength).WithErrorCode("Customer.Note.MaxLength");
    }
}
