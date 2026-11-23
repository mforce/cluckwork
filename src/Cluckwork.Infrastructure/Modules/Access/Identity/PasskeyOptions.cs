using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1282 decision 5: Passkeys:Origins, the operator's list of the origins
// passkeys work at. Unset or empty means passkeys are off.
public sealed class PasskeyOptions
{
    public const string SectionName = "Passkeys";

    public string[] Origins { get; set; } = [];

    // The one origin reader: a scheme, a host and an optional port, with no
    // path, trailing slash, query, fragment, user info or wildcard. Null for
    // anything else. The serving boot refuses an entry it cannot read
    // (EnsurePasskeyOrigins), so Parsed drops none in a running server.
    public static Uri? ParseOrigin(string? value) =>
        value is not null && !value.Contains('*', StringComparison.Ordinal) && !value.EndsWith('/')
        && Uri.TryCreate(value, UriKind.Absolute, out var origin)
        && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0 && origin.UserInfo.Length == 0
            ? origin
            : null;

    private IReadOnlyList<Uri>? _parsed;

    // Read once, after binding.
    internal IReadOnlyList<Uri> Parsed => _parsed ??= [.. Origins.Select(ParseOrigin).OfType<Uri>()];

    // Every route that runs a ceremony answers Passkeys.Off unless the
    // request's host is the host of exactly one configured origin.
    internal bool IsOnFor(HttpContext http) =>
        Parsed.Count(o => string.Equals(o.Host, http.Request.Host.Host, StringComparison.OrdinalIgnoreCase)) == 1;

    // The ValidateOrigin hook: a configured origin (scheme and host without
    // case, port exact) at the request's own host, and never cross-origin. It
    // never reads the Origin header.
    public ValueTask<bool> AcceptsAsync(PasskeyOriginValidationContext context) =>
        ValueTask.FromResult(!context.CrossOrigin
            && ParseOrigin(context.Origin) is { } origin
            && string.Equals(origin.Host, context.HttpContext.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && Parsed.Any(o => string.Equals(o.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(o.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
                && o.Port == origin.Port));
}
