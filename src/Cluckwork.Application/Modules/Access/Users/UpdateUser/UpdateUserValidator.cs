using Cluckwork.Application.Modules.Access.Contracts;
using FluentValidation;

namespace Cluckwork.Application.Modules.Access.Users.UpdateUser;

public sealed class UpdateUserValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserValidator()
    {
        RuleFor(x => x.Name)
            .MaximumLength(Cluckwork.Application.Modules.Access.Users.UserName.MaxLength)
            .WithErrorCode("User.Name.MaxLength")
            .When(x => x.Name is not null);
    }
}
