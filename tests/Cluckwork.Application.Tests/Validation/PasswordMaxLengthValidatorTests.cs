namespace Cluckwork.Application.Tests.Validation;

using Cluckwork.Application.Features.Users;
using Cluckwork.Application.Features.Users.ChangeOwnPassword;
using Cluckwork.Application.Features.Users.CreateUser;
using Cluckwork.Application.Features.Users.SetUserPassword;

// #309 — every credential validator caps its password field at
// PasswordRules.MaxLength so an attacker-controlled oversized password can't be
// pushed into the PBKDF2 hasher. A password ONE char over the cap must fail with
// the field's distinct max-length error code.
public sealed class PasswordMaxLengthValidatorTests
{
    private static string OverCap => new('a', PasswordRules.MaxLength + 1);

    // #309 Fix 8b — the rejection side (257 fails) was already covered; nothing
    // asserted the boundary itself is ACCEPTED, so an off-by-one (MaximumLength
    // silently becoming LessThan, or the constant dropping to 255) could have
    // survived the whole suite invisibly. AtCap is a VALID password shape (mixed
    // case + digit + symbol) so a validator with additional policy rules — not
    // just the length cap — still accepts it at exactly the boundary.
    private static string AtCap => "Aa1!" + new string('a', PasswordRules.MaxLength - 4);

    [Fact]
    public async Task ChangeOwnPassword_CurrentPassword_over_cap_fails_with_max_length_code()
    {
        var validator = new ChangeOwnPasswordValidator();
        // A valid, distinct new password isolates the current-password max rule.
        var cmd = new ChangeOwnPasswordCommand(OverCap, "ValidNewPassw0rd!");

        var result = await validator.ValidateAsync(cmd);

        Assert.Contains(result.Errors, e => e.ErrorCode == "Me.CurrentPassword.MaxLength");
    }

    [Fact]
    public async Task ChangeOwnPassword_NewPassword_over_cap_fails_with_max_length_code()
    {
        var validator = new ChangeOwnPasswordValidator();
        var cmd = new ChangeOwnPasswordCommand("ValidCurrentPass1!", OverCap);

        var result = await validator.ValidateAsync(cmd);

        Assert.Contains(result.Errors, e => e.ErrorCode == "Me.NewPassword.MaxLength");
    }

    [Fact]
    public async Task ChangeOwnPassword_CurrentPassword_at_cap_produces_no_max_length_error()
    {
        var validator = new ChangeOwnPasswordValidator();
        var cmd = new ChangeOwnPasswordCommand(AtCap, "ValidNewPassw0rd!");

        var result = await validator.ValidateAsync(cmd);

        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "Me.CurrentPassword.MaxLength");
    }

    [Fact]
    public async Task ChangeOwnPassword_NewPassword_at_cap_produces_no_max_length_error()
    {
        var validator = new ChangeOwnPasswordValidator();
        var cmd = new ChangeOwnPasswordCommand("ValidCurrentPass1!", AtCap);

        var result = await validator.ValidateAsync(cmd);

        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "Me.NewPassword.MaxLength");
    }

    [Fact]
    public async Task CreateUser_Password_over_cap_fails_with_max_length_code()
    {
        var validator = new CreateUserValidator();
        var cmd = new CreateUserCommand("user@example.com", OverCap, CreateUserValidator.WorkerRole);

        var result = await validator.ValidateAsync(cmd);

        Assert.Contains(result.Errors, e => e.ErrorCode == "User.Password.MaxLength");
    }

    [Fact]
    public async Task CreateUser_Password_at_cap_produces_no_max_length_error()
    {
        var validator = new CreateUserValidator();
        var cmd = new CreateUserCommand("user@example.com", AtCap, CreateUserValidator.WorkerRole);

        var result = await validator.ValidateAsync(cmd);

        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "User.Password.MaxLength");
    }

    [Fact]
    public async Task SetUserPassword_NewPassword_over_cap_fails_with_max_length_code()
    {
        var validator = new SetUserPasswordValidator();
        var cmd = new SetUserPasswordCommand(Guid.NewGuid(), OverCap);

        var result = await validator.ValidateAsync(cmd);

        Assert.Contains(result.Errors, e => e.ErrorCode == "User.NewPassword.MaxLength");
    }

    [Fact]
    public async Task SetUserPassword_NewPassword_at_cap_produces_no_max_length_error()
    {
        var validator = new SetUserPasswordValidator();
        var cmd = new SetUserPasswordCommand(Guid.NewGuid(), AtCap);

        var result = await validator.ValidateAsync(cmd);

        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "User.NewPassword.MaxLength");
    }
}
