namespace Cluckwork.Application.Features.Users.ChangeUserEmail;

using FluentValidation;

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
