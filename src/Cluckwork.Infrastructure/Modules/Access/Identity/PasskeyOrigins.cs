using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1282 decision 5: Passkeys:Origins, the operator's list of the origins
// passkeys work at. Unset or empty means passkeys are off. EnsurePasskeyOrigins
// checks every entry at a serving boot, so the parse here can skip a bad one.
public sealed class PasskeyOptions
{
    public const string SectionName = "Passkeys";

    public string[] Origins { get; set; } = [];

    internal IReadOnlyList<Uri> Parsed() =>
        Origins.Select(o => Uri.TryCreate(o, UriKind.Absolute, out var uri) ? uri : null).OfType<Uri>().ToList();

    // Every route that runs a ceremony answers Passkeys.Off unless the
    // request's host is the host of exactly one configured origin.
    internal bool IsOnFor(HttpContext http) =>
        Parsed().Count(o => string.Equals(o.Host, http.Request.Host.Host, StringComparison.OrdinalIgnoreCase)) == 1;

    // The ValidateOrigin hook: a configured origin (scheme and host without
    // case, port exact) at the request's own host, and never cross-origin. It
    // never reads the Origin header.
    public ValueTask<bool> AcceptsAsync(PasskeyOriginValidationContext context) =>
        ValueTask.FromResult(!context.CrossOrigin
            && Uri.TryCreate(context.Origin, UriKind.Absolute, out var origin)
            && string.Equals(origin.Host, context.HttpContext.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && Parsed().Any(o => string.Equals(o.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(o.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
                && o.Port == origin.Port));
}
