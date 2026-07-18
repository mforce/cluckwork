namespace Cluckwork.Application.Features.Users.CreateUser;

using FluentValidation;

public sealed class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    public const string AdminRole = "Admin";
    public const string WorkerRole = "Worker";

    public CreateUserValidator()
    {
        RuleFor(x => x.Email)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Email is required.")
            .EmailAddress()
            .MaximumLength(256);
        // Identity enforces the full password policy; the minimum length here
        // just gives a clean 400 instead of a Users.CreateFailed round-trip.
        RuleFor(x => x.Password)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Password is required.")
            .MinimumLength(12);
        RuleFor(x => x.Role)
            .Must(r => r is AdminRole or WorkerRole)
            .WithMessage($"Role must be '{AdminRole}' or '{WorkerRole}'.");
    }
}
