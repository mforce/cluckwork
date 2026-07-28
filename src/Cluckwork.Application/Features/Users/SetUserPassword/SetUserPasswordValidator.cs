namespace Cluckwork.Application.Features.Users.SetUserPassword;

using FluentValidation;

public sealed class SetUserPasswordValidator : AbstractValidator<SetUserPasswordCommand>
{
    public SetUserPasswordValidator()
    {
        RuleFor(x => x.NewPassword)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("A new password is required.")
            .WithErrorCode("User.NewPassword.Required")
            .MinimumLength(Cluckwork.Application.Features.Users.PasswordRules.MinLength)
            .WithErrorCode("User.NewPassword.MinLength");
    }
}
