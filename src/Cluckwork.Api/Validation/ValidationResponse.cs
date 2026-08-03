namespace Cluckwork.Api.Validation;

using FluentValidation.Results;

// Additive `errorCodes` companion to the standard `errors` dictionary (#45).
//
// `errors` is unchanged: { field: ["English message", ...] }. `errorCodes` is a
// PARALLEL, index-aligned map { field: ["Feature.Field.Rule" | null, ...] }
// carrying the STABLE machine code for each failure, so a future translation
// layer can key off the code and fall back to the English message.
//
// A code is emitted ONLY when a validator assigned an EXPLICIT one via
// .WithErrorCode("Feature.Field.Rule"). FluentValidation's built-in codes
// ("NotEmptyValidator", "PredicateValidator", …) are framework internals and
// must never leak as a stable contract; they are single PascalCase tokens with
// no dot, which is exactly how we tell them apart — an explicit code always
// contains a '.'. A field with no explicit code on ANY of its failures is
// omitted from errorCodes entirely (its English message still appears in errors).
public static class ValidationResponse
{
    public static IResult Problem(ValidationResult validation)
    {
        var (errors, codes) = Build(validation);
        return codes is null
            ? Results.ValidationProblem(errors)
            : Results.ValidationProblem(
                errors,
                extensions: new Dictionary<string, object?> { ["errorCodes"] = codes });
    }

    // Route a hand-built errors dictionary (a non-FluentValidation 400) through
    // the same shape. These carry no codes, so the output is today's, unchanged.
    public static IResult Problem(IDictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors);

    // #398 review (Codex) — the canonical ValidationProblem shape for a
    // JSON-binding failure (a fractional quantity into an int, an
    // unparseable date/guid, malformed JSON syntax, …): a
    // BadHttpRequestException with StatusCode 400, thrown deep inside
    // minimal-API's generated body-reader before any FluentValidation
    // validator or handler runs. TWO sites render this and must never drift
    // apart, hence the single factory: BindingFailureResponse
    // (Hosting/BindingFailureResponse.cs), the primary path — it intercepts
    // the exception INSIDE UseSerilogRequestLogging so the failure never
    // reaches Serilog's own completion log as a (mis-logged 500/Error)
    // exception; and the `/error` mapping in Program.cs, kept as the
    // backstop for any binding failure that somehow bypasses that
    // middleware (e.g. the response had already started).
    public static IResult BindingFailureProblem() =>
        Problem(new Dictionary<string, string[]>
        {
            ["body"] = ["The request body has an invalid or incorrectly formatted value."],
        });

    // Exposed for unit testing without executing an IResult.
    public static (Dictionary<string, string[]> Errors, Dictionary<string, string?[]>? Codes)
        Build(ValidationResult validation)
    {
        var errors = new Dictionary<string, string[]>();
        var codes = new Dictionary<string, string?[]>();
        foreach (var group in validation.Errors.GroupBy(e => e.PropertyName))
        {
            var failures = group.ToList();
            errors[group.Key] = failures.Select(f => f.ErrorMessage).ToArray();
            if (failures.Any(f => IsExplicit(f.ErrorCode)))
                codes[group.Key] = failures
                    .Select(f => IsExplicit(f.ErrorCode) ? f.ErrorCode : null)
                    .ToArray();
        }
        return (errors, codes.Count == 0 ? null : codes);
    }

    private static bool IsExplicit(string? code) => code is not null && code.Contains('.');
}
