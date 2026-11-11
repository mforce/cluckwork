using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Cluckwork.Api.Middleware;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Modules.Commerce.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Domain.Modules.Farm.Media;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.RateLimiting;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

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
        "Pwd", "Bearer", "Passphrase", "Passcode", "Pin", "Salt", "Jwt",
    };

    // A member that matches a word but holds no credential. On names the type
    // the row applies to, with its subtypes; a null On applies to the member
    // name on every type, so the name itself must say what the value is.
    // Holds is the member's type, nullable unwrapped, that the reason depends
    // on: the row excuses nothing else, so a count's name cannot excuse a list
    // of codes. A string row excuses any string under that name.
    internal sealed record Excuse(Type? On, string Member, Type Holds, string Reason)
    {
        public bool Covers(Type type, MemberInfo member) =>
            member.Name == Member && (On is null || On.IsAssignableFrom(type)) && TypeOf(member) == Holds;

        private static Type TypeOf(MemberInfo member)
        {
            var type = member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;
            return Nullable.GetUnderlyingType(type) ?? type;
        }
    }

    internal static readonly Excuse[] Excused =
    [
        new(null, "CurrencyCode", typeof(string), "an ISO 4217 currency code"),
        new(null, "DefaultCostCurrencyCode", typeof(string), "an ISO 4217 currency code"),
        new(null, "UnitCostCurrencyCode", typeof(string), "an ISO 4217 currency code"),
        new(null, "UnitCode", typeof(string), "the code of an egg unit"),
        new(null, "UnitCode", typeof(EggUnit), "an egg unit"),
        new(null, "DiscountReasonCode", typeof(string), "the code of a discount reason"),
        new(null, "DiscountReasonCode", typeof(DiscountReasonCode), "a discount reason"),
        new(null, "StatusCode", typeof(int), "an HTTP status code"),
        new(null, "RecoveryCodesLeft", typeof(int), "a count of recovery codes, not the codes"),
        new(null, "ConcurrencyStamp", typeof(string), "an optimistic-concurrency value"),
        new(null, "GrantTypes", typeof(string[]), "names of OAuth grant types from client metadata"),
        new(null, "TokenEndpointAuthMethod", typeof(string), "the name of an OAuth client auth method"),
        new(typeof(Error), nameof(Error.Code), typeof(string), "a public error code"),
        new(typeof(CurrencyInfo), nameof(CurrencyInfo.Code), typeof(string), "an ISO 4217 currency code"),
        new(typeof(ImageSanitizer.ImageAssetKind), nameof(ImageSanitizer.ImageAssetKind.Code), typeof(string), "the code of an image kind"),
        new(typeof(OAuthPurgeResult), nameof(OAuthPurgeResult.Tokens), typeof(long), "a count of purged tokens"),
        new(typeof(AppDbContext), nameof(AppDbContext.RefreshTokens), typeof(DbSet<RefreshToken>), "a query root, not a value"),
        new(typeof(AppDbContext), nameof(AppDbContext.OAuthTokens), typeof(DbSet<OpenIddictEntityFrameworkCoreToken<Guid>>), "a query root, not a value"),
        new(typeof(AppDbContext), nameof(AppDbContext.UserTokens), typeof(DbSet<IdentityUserToken<Guid>>), "a query root, not a value"),
        new(typeof(AppDbContext), nameof(AppDbContext.DataProtectionKeys), typeof(DbSet<DataProtectionKey>), "a query root, not a value"),
        new(typeof(SlotGrant), nameof(SlotGrant.Key), typeof(string), "the rate-limit slot's lease key"),
        new(typeof(IdempotencyScope), nameof(IdempotencyScope.KeyHash), typeof(string), "a hash of the Idempotency-Key header"),
        new(OpenApiResponseComment, "Code", typeof(string), "the HTTP status of a response, read from XML docs by the OpenAPI source generator"),
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
    // and not compiler-generated or nested in a compiler-generated type (a
    // lambda's async state machine sits in its closure class and carries no
    // attribute itself). Open generic types are kept.
    public static IEnumerable<Type> Walk(IEnumerable<Assembly> assemblies) =>
        assemblies.SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsInterface && !type.IsAbstract && !CompilerGenerated(type));

    private static bool CompilerGenerated(Type type)
    {
        for (var current = type; current is not null; current = current.DeclaringType)
            if (current.IsDefined(typeof(CompilerGeneratedAttribute), false))
                return true;
        return false;
    }

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
        WordPattern().Matches(name).Concat(AcronymPluralPattern().Matches(name)).Any(word => Words.Contains(word.Value)
            || word.Value.EndsWith("s", StringComparison.OrdinalIgnoreCase) && Words.Contains(word.Value[..^1]));

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

    // A name matches when a word from either pattern does: the second only adds
    // an acronym's plural (JWTs), so every word the first yields is still read.
    [GeneratedRegex("[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+")]
    private static partial Regex WordPattern();

    [GeneratedRegex("[A-Z]+s(?![a-z])")]
    private static partial Regex AcronymPluralPattern();
}
