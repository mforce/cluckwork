using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// G3.29: the ToString a type resolves to must return the constant
// "[redacted]" and nothing else. That is read from the method's IL, so no
// state of any member can make it print a secret. It proves the constant only:
// another print path, such as serializing the record, is not covered. A type
// that formats itself is refused, because interpolation, string.Format and
// the logger formatter call its format method instead of ToString.
internal static class RedactedToString
{
    private const string Redacted = "[redacted]";

    // Every one-byte op code, so each instruction is decoded with its real
    // operand size. A two-byte op code starts with the 0xFE prefix, which is
    // in this table as a one-byte prefix and is never allowed.
    private static readonly Dictionary<byte, OpCode> OneByte = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .Where(op => op.Size == 1)
        .ToDictionary(op => (byte)op.Value);

    // What a body that only returns a constant compiles to, in Debug and in
    // Release: an optional nop, the ldstr, and a direct ret or the Debug
    // store, branch and load before it.
    private static readonly HashSet<short> Allowed =
        [OpCodes.Nop.Value, OpCodes.Ldstr.Value, OpCodes.Stloc_0.Value, OpCodes.Br_S.Value, OpCodes.Ldloc_0.Value, OpCodes.Ret.Value];

    public static void AssertConstant(Type type)
    {
        Assert.False(typeof(IFormattable).IsAssignableFrom(type) || typeof(ISpanFormattable).IsAssignableFrom(type)
            || typeof(IUtf8SpanFormattable).IsAssignableFrom(type),
            $"{type} formats itself, so it can print its members past ToString");
        var toString = type.GetMethod(nameof(ToString), BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!;
        // Only for the message: the IL check below also fails an inherited object.ToString.
        Assert.True(toString.DeclaringType != typeof(object) && toString.DeclaringType != typeof(ValueType),
            $"{type} does not override ToString");
        Assert.True(ReturnsOnlyTheConstant(toString.GetMethodBody()?.GetILAsByteArray() ?? [], toString.Module),
            $"{type}'s ToString ({toString.DeclaringType}) does more than return \"{Redacted}\"");
        // A wrong IL read cannot pass silently: the method must also answer the constant.
        Assert.Equal(Redacted, RuntimeHelpers.GetUninitializedObject(type).ToString());
    }

    internal static bool ReturnsOnlyTheConstant(byte[] il, Module module)
    {
        var strings = 0;
        for (var i = 0; i < il.Length;)
        {
            if (!OneByte.TryGetValue(il[i], out var op)) return false;
            var operand = OperandSize(op);
            if (operand < 0 || i + 1 + operand > il.Length) return false;
            if (IsCoverageHit(il, i, module))
            {
                i += 10;
                continue;
            }
            if (!Allowed.Contains(op.Value)) return false;
            if (op == OpCodes.Ldstr)
            {
                if (module.ResolveString(BitConverter.ToInt32(il, i + 1)) != Redacted) return false;
                strings++;
            }
            i += 1 + operand;
        }
        return strings == 1;
    }

    // Coverlet's hit counter, which coverage runs inject into every method of
    // an included assembly: ldc.i4 <id>, then a call to the static
    // RecordHit(int) of the tracker type it adds to that same assembly.
    private static bool IsCoverageHit(byte[] il, int i, Module module)
    {
        if (il[i] != OpCodes.Ldc_I4.Value || i + 10 > il.Length || il[i + 5] != OpCodes.Call.Value) return false;
        return module.ResolveMethod(BitConverter.ToInt32(il, i + 6)) is MethodInfo { IsStatic: true, Name: "RecordHit" } hit
            && hit.ReturnType == typeof(void)
            && hit.GetParameters() is [{ ParameterType: var parameter }] && parameter == typeof(int)
            && hit.DeclaringType?.FullName?.StartsWith("Coverlet.Core.Instrumentation.Tracker.", StringComparison.Ordinal) == true
            && hit.DeclaringType.Assembly == module.Assembly;
    }

    private static int OperandSize(OpCode op) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => -1,
        _ => 4,
    };
}
