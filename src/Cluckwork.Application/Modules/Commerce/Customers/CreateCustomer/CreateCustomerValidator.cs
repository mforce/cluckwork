using Cluckwork.Application.Modules.Commerce.Contracts;
using Cluckwork.Domain.Modules.Commerce.Sales;
using FluentValidation;

namespace Cluckwork.Application.Modules.Commerce.Customers.CreateCustomer;

public sealed class CreateCustomerValidator : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerValidator()
    {
        // NotEmpty alone lets "   " through to the domain guard (500, not 400).
        RuleFor(x => x.Name)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Name is required.")
            .WithErrorCode("Customer.Name.Required")
            .MaximumLength(Customer.MaxNameLength)
            .WithErrorCode("Customer.Name.MaxLength");
        RuleFor(x => x.Phone)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Phone is required.")
            .WithErrorCode("Customer.Phone.Required")
            .MaximumLength(Customer.MaxPhoneLength)
            .WithErrorCode("Customer.Phone.MaxLength");
        RuleFor(x => x.Email).EmailAddress().WithErrorCode("Customer.Email.Format")
            .MaximumLength(Customer.MaxEmailLength).WithErrorCode("Customer.Email.MaxLength")
            .When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Address).MaximumLength(Customer.MaxAddressLength)
            .WithErrorCode("Customer.Address.MaxLength");
        RuleFor(x => x.Note).MaximumLength(Customer.MaxNoteLength)
            .WithErrorCode("Customer.Note.MaxLength");
    }
}
