namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Application.Features.Users;

// #309 — the login validator is MAX-length only: an oversized email/password is
// rejected (so it never reaches the PBKDF2 verify), but an EMPTY credential must
// still validate so it flows to LoginAsync and returns the generic 401. This is a
// plain validator unit test (no DB / no test host), so it lives outside the
// integration collection.
public sealed class LoginRequestValidatorTests
{
    private readonly LoginRequestValidator _validator = new();

    [Fact]
    public void Password_over_256_is_rejected_with_max_length_code()
    {
        var result = _validator.Validate(
            new LoginRequest("default-farm", "user@example.com", new string('a', PasswordRules.MaxLength + 1)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorCode == "Auth.Password.MaxLength");
    }

    [Fact]
    public void Email_over_256_is_rejected_with_max_length_code()
    {
        var result = _validator.Validate(
            new LoginRequest("default-farm", new string('a', 257), "ValidPassw0rd!"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorCode == "Auth.Email.MaxLength");
    }

    [Fact]
    public void Empty_credentials_are_accepted_so_they_flow_to_the_generic_401()
    {
        // Empty is NOT the validator's concern — the endpoint must still call
        // LoginAsync and return the non-enumerating 401, unchanged by #309.
        var result = _validator.Validate(new LoginRequest("default-farm", "", ""));

        Assert.True(result.IsValid);
    }

    // #309 Fix 8b — the 256 boundary was tested only on the rejection side (257
    // fails, above); nothing asserted exactly 256 is ACCEPTED, so an off-by-one
    // (MaximumLength(256) silently becoming e.g. LessThan(256)) could pass the
    // whole suite invisibly.
    [Fact]
    public void Email_at_256_validates_with_no_max_length_error()
    {
        var result = _validator.Validate(new LoginRequest("default-farm", new string('a', 256), "ValidPassw0rd!"));

        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "Auth.Email.MaxLength");
    }

    [Fact]
    public void Password_at_256_validates_with_no_max_length_error()
    {
        var result = _validator.Validate(
            new LoginRequest("default-farm", "user@example.com", new string('a', PasswordRules.MaxLength)));

        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "Auth.Password.MaxLength");
    }

    // #309 Fix 7 — a JSON body with an explicit `null` for either field bound
    // LoginRequest with a null Email/Password (MaximumLength tolerates null),
    // which then reached UserManager.FindByEmailAsync / CheckPasswordAsync /
    // VerifyHashedPassword and threw ArgumentNullException — uncaught by
    // /error's exception switch, so it fell through to an unhandled 500. The
    // not-null guard rejects it with a clean 400 instead.
    [Fact]
    public void Null_email_is_rejected_with_a_required_code()
    {
        var result = _validator.Validate(new LoginRequest("default-farm", null!, "ValidPassw0rd!"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorCode == "Auth.Email.Required");
    }

    [Fact]
    public void Null_password_is_rejected_with_a_required_code()
    {
        var result = _validator.Validate(new LoginRequest("default-farm", "user@example.com", null!));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorCode == "Auth.Password.Required");
    }
}
