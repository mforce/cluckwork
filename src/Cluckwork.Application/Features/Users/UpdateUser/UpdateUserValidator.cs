namespace Cluckwork.Application.Features.Users.UpdateUser;

using FluentValidation;

public sealed class UpdateUserValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserValidator()
    {
        RuleFor(x => x.Name)
            .MaximumLength(Cluckwork.Application.Features.Users.UserName.MaxLength)
            .When(x => x.Name is not null);
    }
}
