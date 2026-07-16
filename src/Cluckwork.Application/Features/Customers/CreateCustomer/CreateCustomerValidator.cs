namespace Cluckwork.Application.Features.Customers.CreateCustomer;

using Cluckwork.Domain.Sales;
using FluentValidation;

public sealed class CreateCustomerValidator : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Customer.MaxNameLength);
        RuleFor(x => x.Phone).NotEmpty().MaximumLength(Customer.MaxPhoneLength);
        RuleFor(x => x.Email).EmailAddress().MaximumLength(Customer.MaxEmailLength)
            .When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Address).MaximumLength(Customer.MaxAddressLength);
        RuleFor(x => x.Note).MaximumLength(Customer.MaxNoteLength);
    }
}
