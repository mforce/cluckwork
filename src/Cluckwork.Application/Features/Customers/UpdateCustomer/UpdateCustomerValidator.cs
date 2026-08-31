namespace Cluckwork.Application.Features.Customers.UpdateCustomer;

using Cluckwork.Domain.Sales;
using FluentValidation;

public sealed class UpdateCustomerValidator : AbstractValidator<UpdateCustomerCommand>
{
    public UpdateCustomerValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty().WithErrorCode("Customer.CustomerId.Required");
        RuleFor(x => x.Version).GreaterThanOrEqualTo(0).WithErrorCode("Customer.Version.NonNegative");
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
