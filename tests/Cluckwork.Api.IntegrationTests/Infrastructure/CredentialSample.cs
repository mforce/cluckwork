using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// G3.29: an instance whose every member holds a value generated here, so a
// ToString that prints any member, or prints one only when it is set or long
// enough, shows it. Every member is filled or named below as harmless; any
// other shape throws, so a new one fails loudly until this learns it.
internal static class CredentialSample
{
    // Scalars that cannot hold a secret, each with its reason.
    private static readonly Dictionary<Type, string> Harmless = new()
    {
        [typeof(bool)] = "a flag",
        [typeof(DateTimeOffset)] = "a time: an expiry or an issue time",
        [typeof(Guid)] = "an id; the guards do not treat user or account ids as credentials",
    };

    private const int ShortLength = 32;
    private const int LongLength = 96;

    // Asserts the type prints exactly "[redacted]", filled with short values
    // and again with long ones.
    public static void AssertRedacted(Type type)
    {
        foreach (var length in new[] { ShortLength, LongLength })
            Assert.True(Fill(type, length, depth: 0).ToString() == "[redacted]", $"{type} prints its members ({length}-character values)");
    }

    private static object Fill(Type type, int length, int depth)
    {
        if (depth > 3)
            throw new NotSupportedException($"{type} nests records deeper than CredentialSample fills");
        var instance = RuntimeHelpers.GetUninitializedObject(type);
        for (var declaring = type; declaring is not null && declaring != typeof(object); declaring = declaring.BaseType)
            foreach (var field in declaring.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                // An enum is a closed set of names, never a secret.
                if (!Harmless.ContainsKey(field.FieldType) && !field.FieldType.IsEnum)
                    field.SetValue(instance, ValueFor(field.FieldType, length, depth, $"{type}.{field.Name}"));
        return instance;
    }

    private static object ValueFor(Type type, int length, int depth, string member)
    {
        if (type == typeof(string)) return NewString(length);
        if (type == typeof(string[]) || type == typeof(IReadOnlyList<string>)) return new[] { NewString(length), NewString(length) };
        // A TOTP code or a counter: nonzero, so a ToString that hides only 0 shows it.
        if (type == typeof(int)) return RandomNumberGenerator.GetInt32(1, 1_000_000);
        if (type == typeof(long)) return (long)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return Harmless.ContainsKey(underlying) ? Activator.CreateInstance(underlying)! : ValueFor(underlying, length, depth, member);
        if (!type.IsAbstract && type.Namespace?.StartsWith("Cluckwork", StringComparison.Ordinal) == true)
            return Fill(type, length, depth + 1);
        throw new NotSupportedException($"{member} is a {type}, which CredentialSample cannot fill; extend it");
    }

    private static string NewString(int length) => RandomNumberGenerator.GetHexString(length);
}
