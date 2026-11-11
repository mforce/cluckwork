using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Cluckwork.Api.Middleware;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Domain.Modules.Farm.Media;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.RateLimiting;
using Microsoft.AspNetCore.Identity;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// G3.29 (#1397): the types that carry a credential, found from what each type
// declares: a member whose name has a credential word, or the [Credential]
// marker on the type or a base. A found type must print nothing but its type
// name (object's or ValueType's ToString, or a framework ToString reviewed
// below) or pass RedactedToString.AssertConstant.
internal static partial class CredentialTypes
{
    // Matched against each PascalCase word of a member name, singular or plural.
    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "Password", "Token", "Secret", "Challenge", "Grant", "Key", "Code", "Stamp", "Pem",
        "Otp", "Totp", "Verifier", "Nonce", "Signature", "Cookie", "Credential", "Proof",
    };

    // A member that matches a word but holds no credential. On names the type
    // the row applies to, with its subtypes; a null On applies to the member
    // name on every type, so the name itself must say what the value is.
    internal sealed record Excuse(Type? On, string Member, string Reason)
    {
        public bool Covers(Type type, MemberInfo member) =>
            member.Name == Member && (On is null || On.IsAssignableFrom(type));
    }

    internal static readonly Excuse[] Excused =
    [
        new(null, "CurrencyCode", "an ISO 4217 currency code"),
        new(null, "DefaultCostCurrencyCode", "an ISO 4217 currency code"),
        new(null, "UnitCostCurrencyCode", "an ISO 4217 currency code"),
        new(null, "UnitCode", "the code of an egg unit"),
        new(null, "DiscountReasonCode", "the code of a discount reason"),
        new(null, "StatusCode", "an HTTP status code"),
        new(null, "RecoveryCodesLeft", "a count of recovery codes, not the codes"),
        new(null, "MustChangePassword", "a flag"),
        new(null, "ConcurrencyStamp", "an optimistic-concurrency value"),
        new(null, "GrantTypes", "names of OAuth grant types from client metadata"),
        new(null, "TokenEndpointAuthMethod", "the name of an OAuth client auth method"),
        new(typeof(Error), nameof(Error.Code), "a public error code"),
        new(typeof(CurrencyInfo), nameof(CurrencyInfo.Code), "an ISO 4217 currency code"),
        new(typeof(ImageSanitizer.ImageAssetKind), nameof(ImageSanitizer.ImageAssetKind.Code), "the code of an image kind"),
        new(typeof(OAuthPurgeResult), nameof(OAuthPurgeResult.Tokens), "a count of purged tokens"),
        new(typeof(AppDbContext), nameof(AppDbContext.RefreshTokens), "a query root, not a value"),
        new(typeof(AppDbContext), nameof(AppDbContext.OAuthTokens), "a query root, not a value"),
        new(typeof(AppDbContext), nameof(AppDbContext.UserTokens), "a query root, not a value"),
        new(typeof(AppDbContext), nameof(AppDbContext.DataProtectionKeys), "a query root, not a value"),
        new(typeof(SlotGrant), nameof(SlotGrant.Key), "the rate-limit slot's lease key"),
        new(typeof(IdempotencyScope), nameof(IdempotencyScope.KeyHash), "a hash of the Idempotency-Key header"),
        new(OpenApiResponseComment, "Code", "the HTTP status of a response, read from XML docs by the OpenAPI source generator"),
    ];

    // A file-local record the Microsoft.AspNetCore.OpenApi source generator
    // emits into the Api assembly; its name carries a per-file hash.
    private static Type OpenApiResponseComment => typeof(IdempotencyScope).Assembly.GetTypes()
        .Single(type => type.Namespace == "Microsoft.AspNetCore.OpenApi.Generated" && type.Name.EndsWith("__XmlResponseComment", StringComparison.Ordinal));

    // A ToString a framework base declares, read and found to print no member
    // the word list catches. It excuses a type only while the type resolves
    // ToString to it, so a subtype that declares its own is checked again.
    internal static readonly Dictionary<Type, string> ReviewedToStrings = new()
    {
        [typeof(IdentityUser<Guid>)] = "returns UserName and nothing else, so PasswordHash and SecurityStamp never print",
    };

    // Every type the walk reads: not an interface, not abstract (an abstract
    // type's members print through its concrete subtypes, which are walked),
    // and not compiler-generated. Open generic types are kept.
    public static IEnumerable<Type> Walk(IEnumerable<Assembly> assemblies) =>
        assemblies.SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsInterface && !type.IsAbstract && !type.IsDefined(typeof(CompilerGeneratedAttribute), false));

    public static List<Type> Find(IEnumerable<Assembly> assemblies) =>
        [.. Walk(assemblies).Where(IsCredentialType)];

    public static bool IsCredentialType(Type type) =>
        type.IsDefined(typeof(CredentialAttribute), inherit: true)
        || Members(type).Any(member => NamesACredential(member.Name) && !Excused.Any(excuse => excuse.Covers(type, member)));

    // Every instance property and field, a base's private ones included, and
    // a captured primary-constructor parameter (<name>P). A property's backing
    // field is skipped, because the property is read.
    public static IEnumerable<MemberInfo> Members(Type type)
    {
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
            foreach (var member in current.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (member is PropertyInfo || member is FieldInfo && !member.Name.EndsWith(">k__BackingField", StringComparison.Ordinal))
                    yield return member;
    }

    public static bool NamesACredential(string name) =>
        WordPattern().Matches(name).Any(word => Words.Contains(word.Value)
            || word.Value.EndsWith('s') && Words.Contains(word.Value[..^1]));

    // An open generic type is checked closed over object: the IL is the
    // definition's, and the runtime call needs an instance.
    public static void AssertRedacted(Type type)
    {
        var closed = type.IsGenericTypeDefinition
            ? type.MakeGenericType([.. type.GetGenericArguments().Select(_ => typeof(object))])
            : type;
        var printer = closed.GetMethod(nameof(ToString), BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!.DeclaringType!;
        if (printer == typeof(object) || printer == typeof(ValueType) || ReviewedToStrings.ContainsKey(printer))
            RedactedToString.AssertDoesNotFormat(closed);
        else
            RedactedToString.AssertConstant(closed);
    }

    [GeneratedRegex("[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+")]
    private static partial Regex WordPattern();
}
