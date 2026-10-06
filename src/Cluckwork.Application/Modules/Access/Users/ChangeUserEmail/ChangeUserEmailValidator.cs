using Cluckwork.Application.Modules.Access.Contracts;
using FluentValidation;

namespace Cluckwork.Application.Modules.Access.Users.ChangeUserEmail;

public sealed class ChangeUserEmailValidator : AbstractValidator<ChangeUserEmailCommand>
{
    public ChangeUserEmailValidator()
    {
        RuleFor(x => x.Email == null ? string.Empty : x.Email.Trim())
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Email is required.")
            .WithErrorCode("User.Email.Required")
            .EmailAddress()
            .WithErrorCode("User.Email.Format")
            .MaximumLength(256)
            .WithErrorCode("User.Email.MaxLength")
            .OverridePropertyName(nameof(ChangeUserEmailCommand.Email));
    }
}
