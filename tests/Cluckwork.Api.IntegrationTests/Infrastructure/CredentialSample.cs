using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// G3.29: a ToString must print "[redacted]" whatever its members hold. The
// base sample sets every member; then each member, one at a time and at any
// depth, visits each of its states while the rest keep their base values, and
// every visit must print exactly "[redacted]". A member of a shape this does
// not know throws and names it. Out of scope: a secret printed only when two
// members are in particular states at once.
internal static class CredentialSample
{
    private static readonly NullabilityInfoContext Nullability = new();

    public static void AssertRedacted(Type type)
    {
        Assert.True(Build(type, visit: null).ToString() == "[redacted]", $"{type} prints its members when every member is set");
        foreach (var (path, state) in Visits(type, prefix: type.Name, depth: 0))
            Assert.True(Build(type, (path, state)).ToString() == "[redacted]",
                $"{type} prints its members when {path} is {Describe(state)}");
    }

    private sealed record Leaf(string Path, Type Type, bool Nullable);

    // Every member's path and each state it visits.
    private static IEnumerable<(string Path, object? State)> Visits(Type type, string prefix, int depth)
    {
        foreach (var field in Fields(type, depth))
        {
            var path = $"{prefix}.{field.Name}";
            var (shape, nullable) = Shape(field);
            if (nullable) yield return (path, null);
            if (IsRecord(shape))
            {
                foreach (var nested in Visits(shape, path, depth + 1)) yield return nested;
                continue;
            }
            foreach (var state in States(shape, path)) yield return (path, state);
        }
    }

    // The base sample, with one member at the visited path set to the visited state.
    private static object Build(Type type, (string Path, object? State)? visit, string? prefix = null, int depth = 0)
    {
        prefix ??= type.Name;
        var instance = RuntimeHelpers.GetUninitializedObject(type);
        foreach (var field in Fields(type, depth))
        {
            var path = $"{prefix}.{field.Name}";
            var (shape, _) = Shape(field);
            var value = visit is { } v && v.Path == path ? v.State
                : IsRecord(shape) ? Build(shape, visit, path, depth + 1)
                : States(shape, path).Last();
            field.SetValue(instance, value);
        }
        return instance;
    }

    private static IEnumerable<FieldInfo> Fields(Type type, int depth)
    {
        if (depth > 3)
            throw new NotSupportedException($"{type} nests records deeper than CredentialSample visits");
        for (var declaring = type; declaring is not null && declaring != typeof(object); declaring = declaring.BaseType)
            foreach (var field in declaring.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                yield return field;
    }

    // The member's type with any Nullable<T> removed, and whether it can be null.
    private static (Type Shape, bool Nullable) Shape(FieldInfo field)
    {
        if (System.Nullable.GetUnderlyingType(field.FieldType) is { } underlying) return (underlying, true);
        var nullable = !field.FieldType.IsValueType && Nullability.Create(field).WriteState == NullabilityState.Nullable;
        return (field.FieldType, nullable);
    }

    private static bool IsRecord(Type type) =>
        !type.IsAbstract && !type.IsEnum && type.Namespace?.StartsWith("Cluckwork", StringComparison.Ordinal) == true;

    // The states a member visits; the last is its base value.
    private static IReadOnlyList<object> States(Type type, string path)
    {
        if (type == typeof(string)) return ["", Hex(96), Hex(32)];
        if (type == typeof(string[]) || type == typeof(IReadOnlyList<string>))
            return [Array.Empty<string>(), new[] { Hex(96), Hex(96) }, new[] { Hex(32), Hex(32) }];
        if (type == typeof(bool)) return [false, true];
        if (type == typeof(int)) return [0, RandomNumberGenerator.GetInt32(1, 1_000_000)];
        if (type == typeof(long)) return [0L, (long)RandomNumberGenerator.GetInt32(1, int.MaxValue)];
        if (type == typeof(Guid)) return [Guid.Empty, Guid.NewGuid()];
        if (type == typeof(DateTimeOffset))
            return [default(DateTimeOffset), DateTimeOffset.UnixEpoch.AddSeconds(RandomNumberGenerator.GetInt32(1, int.MaxValue))];
        if (type.IsEnum)
        {
            var defined = Enum.GetValues(type).Cast<object>().ToList();
            var undefined = Enum.ToObject(type, defined.Select(Convert.ToInt64).DefaultIfEmpty(0).Max() + 1);
            return [.. defined, undefined];
        }
        throw new NotSupportedException($"{path} is a {type}, which CredentialSample cannot visit; extend it");
    }

    private static string Hex(int length) => RandomNumberGenerator.GetHexString(length);

    private static string Describe(object? state) => state switch
    {
        null => "null",
        string s => $"a {s.Length}-character string",
        string[] a => $"{a.Length} strings",
        _ => state.ToString() ?? "",
    };
}
