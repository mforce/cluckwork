using FluentValidation;

namespace Cluckwork.Application.Features.Users.UpdateUser;

public sealed class UpdateUserValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserValidator()
    {
        RuleFor(x => x.Name)
            .MaximumLength(Cluckwork.Application.Features.Users.UserName.MaxLength)
            .WithErrorCode("User.Name.MaxLength")
            .When(x => x.Name is not null);
    }
}
