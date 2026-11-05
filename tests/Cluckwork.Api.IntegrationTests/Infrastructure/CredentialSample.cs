using System.Reflection;
using System.Runtime.CompilerServices;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// G3.29: an instance whose every string, string list and nested Cluckwork
// record holds a value generated here, so a ToString that prints any member,
// or prints one only when it is set, shows a value the test can look for.
internal static class CredentialSample
{
    public static (object Instance, IReadOnlyList<string> Values) Build(Type type)
    {
        var values = new List<string>();
        return (Fill(type, values, depth: 0), values);
    }

    // Asserts the type prints exactly "[redacted]" and none of its values.
    public static void AssertRedacted(Type type)
    {
        var (instance, values) = Build(type);
        var printed = instance.ToString();
        Assert.True(printed == "[redacted]", $"{type} prints its members");
        Assert.True(values.Count > 0 || type.IsValueType || IsCodeHolder(type), $"{type} has no member to fill; extend CredentialSample");
        foreach (var value in values)
            Assert.DoesNotContain(value, printed, StringComparison.Ordinal);
    }

    // A TOTP code is an int, which the redaction must hide too, but no
    // generated string can stand for it.
    private static bool IsCodeHolder(Type type) =>
        type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Any(f => f.FieldType == typeof(int));

    private static object Fill(Type type, List<string> values, int depth)
    {
        var instance = RuntimeHelpers.GetUninitializedObject(type);
        for (var declaring = type; declaring is not null && declaring != typeof(object); declaring = declaring.BaseType)
            foreach (var field in declaring.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (ValueFor(field.FieldType, values, depth) is { } value)
                    field.SetValue(instance, value);
        return instance;
    }

    private static object? ValueFor(Type type, List<string> values, int depth)
    {
        if (type == typeof(string)) return NewValue(values);
        if (type != typeof(object) && type.IsAssignableFrom(typeof(string[]))) return new[] { NewValue(values), NewValue(values) };
        if (depth < 3 && !type.IsAbstract && !type.IsEnum && !type.IsPrimitive
            && type.Namespace?.StartsWith("Cluckwork", StringComparison.Ordinal) == true)
            return Fill(type, values, depth + 1);
        return null;
    }

    private static string NewValue(List<string> values)
    {
        var value = Guid.NewGuid().ToString("N");
        values.Add(value);
        return value;
    }
}
