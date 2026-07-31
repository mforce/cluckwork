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
            .WithErrorCode("User.NewPassword.MinLength")
            // #309 — bound the credential ahead of the PBKDF2 hash.
            .MaximumLength(Cluckwork.Application.Features.Users.PasswordRules.MaxLength)
            .WithErrorCode("User.NewPassword.MaxLength");
    }
}
