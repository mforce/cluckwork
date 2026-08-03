namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.Hosting;
using Cluckwork.Api.Validation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

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

// #398 review rounds 4-7 — BindingFailureResponse.ConcernsRequestBody decides
// whether a 400 binding failure is reported under `body` or `query`. It took
// four review rounds to get right, each earlier version wrong in a different
// direction, so the rules are pinned here as pure functions rather than only
// through the endpoints that happen to exercise them.
public sealed class ConcernsRequestBodyTests
{
    private static Endpoint EndpointWith(params object[] metadata) =>
        new(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test");

    private static HttpContext Context(Endpoint? endpoint, long? contentLength = null)
    {
        var context = new DefaultHttpContext();
        if (endpoint is not null) context.SetEndpoint(endpoint);
        context.Request.ContentLength = contentLength;
        return context;
    }

    // Round 4: a bodyless GET whose typed query parameter failed to bind must
    // not be told its body was malformed.
    [Fact]
    public void Query_only_endpoint_is_not_a_body_failure() =>
        Assert.False(BindingFailureResponse.ConcernsRequestBody(Context(EndpointWith())));

    // Round 5: a caller who omits a REQUIRED body sends no bytes, yet that is a
    // body failure — so the ENDPOINT's contract decides, not the byte count.
    [Fact]
    public void Endpoint_declaring_a_body_is_a_body_failure_even_with_no_bytes() =>
        Assert.True(BindingFailureResponse.ConcernsRequestBody(
            Context(EndpointWith(new AcceptsMetadata(["application/json"], typeof(object))))));

    // Round 6: incidental payload bytes must NOT override a matched endpoint
    // that accepts no body. This is the case an OR-ed expression got wrong.
    [Fact]
    public void Stray_bytes_do_not_override_a_query_only_endpoint() =>
        Assert.False(BindingFailureResponse.ConcernsRequestBody(
            Context(EndpointWith(), contentLength: 42)));

    // Round 7: an endpoint that reads the body MANUALLY declares no
    // IAcceptsMetadata, but is still a body endpoint (raw logo upload,
    // client-error report).
    [Fact]
    public void Manually_read_body_endpoint_is_a_body_failure() =>
        Assert.True(BindingFailureResponse.ConcernsRequestBody(
            Context(EndpointWith(new ReadsRequestBodyAttribute()))));

    // Only with NO matched endpoint does the byte fallback apply.
    [Fact]
    public void Without_an_endpoint_the_byte_fallback_decides()
    {
        Assert.True(BindingFailureResponse.ConcernsRequestBody(Context(null, contentLength: 1)));
        Assert.False(BindingFailureResponse.ConcernsRequestBody(Context(null)));
    }
}
