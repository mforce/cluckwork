namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.Validation;
using FluentValidation.Results;

// Pure-function tests: no server, no Testcontainers collection.
public sealed class ValidationResponseTests
{
    private static ValidationResult Result(params ValidationFailure[] failures) => new(failures);

    private static ValidationFailure Fail(string prop, string msg, string? code)
    {
        var f = new ValidationFailure(prop, msg);
        if (code is not null) f.ErrorCode = code;
        return f;
    }

    [Fact]
    public void Errors_mirror_every_failure()
    {
        var (errors, _) = ValidationResponse.Build(Result(
            Fail("Language", "bad", "Me.Language.Format"),
            Fail("Name", "required", "NotEmptyValidator")));
        Assert.Equal(["bad"], errors["Language"]);
        Assert.Equal(["required"], errors["Name"]);
    }

    [Fact]
    public void Explicit_code_is_emitted()
    {
        var (_, codes) = ValidationResponse.Build(Result(
            Fail("Language", "bad", "Me.Language.Format")));
        Assert.NotNull(codes);
        Assert.True(
            new string?[] { "Me.Language.Format" }.SequenceEqual(codes!["Language"]),
            $"Expected [Me.Language.Format], got [{string.Join(", ", codes!["Language"].Select(c => c ?? "null"))}]");
    }

    [Fact]
    public void Default_framework_code_never_leaks()
    {
        // FluentValidation's built-in code is a single dot-less token.
        var (_, codes) = ValidationResponse.Build(Result(
            Fail("Name", "required", "NotEmptyValidator")));
        Assert.Null(codes); // no field had an explicit code → no errorCodes at all
    }

    [Fact]
    public void Mixed_field_keeps_index_alignment_with_null_for_uncoded()
    {
        // One field, two failures: first coded, second a framework default.
        var (errors, codes) = ValidationResponse.Build(Result(
            Fail("Language", "bad format", "Me.Language.Format"),
            Fail("Language", "too long", "MaximumLengthValidator")));
        Assert.Equal(["bad format", "too long"], errors["Language"]);
        Assert.NotNull(codes);
        Assert.True(
            new string?[] { "Me.Language.Format", null }.SequenceEqual(codes!["Language"]),
            $"Expected [Me.Language.Format, null], got [{string.Join(", ", codes!["Language"].Select(c => c ?? "null"))}]");
    }
}
