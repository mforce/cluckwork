namespace Cluckwork.Application.Features.Customers.CreateCustomer;

using Cluckwork.Domain.Sales;
using FluentValidation;

public sealed class CreateCustomerValidator : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerValidator()
    {
        // NotEmpty alone lets "   " through to the domain guard (500, not 400).
        RuleFor(x => x.Name)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Name is required.")
            .MaximumLength(Customer.MaxNameLength);
        RuleFor(x => x.Phone)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Phone is required.")
            .MaximumLength(Customer.MaxPhoneLength);
        RuleFor(x => x.Email).EmailAddress().MaximumLength(Customer.MaxEmailLength)
            .When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Address).MaximumLength(Customer.MaxAddressLength);
        RuleFor(x => x.Note).MaximumLength(Customer.MaxNoteLength);
    }
}
