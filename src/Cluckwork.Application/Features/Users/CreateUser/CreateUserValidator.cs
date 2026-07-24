namespace Cluckwork.Application.Features.Users.CreateUser;

using FluentValidation;

public sealed class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    public const string AdminRole = Cluckwork.Domain.Accounts.Roles.Owner;
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
            .Must(r => r == WorkerRole || Cluckwork.Domain.Accounts.Roles.Assignable.Contains(r))
            .WithMessage("Role must be Admin (owner), Manager, Sales, ReadOnly, or Worker.");
        // #163 — Name is optional; only its length is bounded.
        RuleFor(x => x.Name)
            .MaximumLength(Cluckwork.Application.Features.Users.UserName.MaxLength)
            .When(x => x.Name is not null);
    }
}
