namespace Cluckwork.Domain.Common;

public record Error(string Code, string Description)
{
    public static readonly Error None = new(string.Empty, string.Empty);

    public static Error NotFound(string resource, object id) =>
        new($"{resource}.NotFound", $"{resource} '{id}' was not found.");

    public static Error Conflict(string code, string description) =>
        new(code, description);

    public static Error Validation(string code, string description) =>
        new(code, description);

    public static Error Domain(string code, string description) =>
        new(code, description);
}
