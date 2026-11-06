using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// G3.29: the ToString a type resolves to must return the constant
// "[redacted]" and nothing else. That is read from the method's IL, so no
// state of any member can make it print a secret. It proves the constant only:
// another print path, such as serializing the record, is not covered.
internal static class RedactedToString
{
    private const string Redacted = "[redacted]";

    // The op codes a body that only returns a constant compiles to, in Debug
    // and in Release: an optional nop, the ldstr, and a direct ret or the Debug
    // store, branch and load before it.
    private static readonly HashSet<short> Allowed =
        [OpCodes.Nop.Value, OpCodes.Ldstr.Value, OpCodes.Stloc_0.Value, OpCodes.Br_S.Value, OpCodes.Ldloc_0.Value, OpCodes.Ret.Value];

    public static void AssertConstant(Type type)
    {
        var toString = type.GetMethod(nameof(ToString), BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!;
        Assert.True(toString.DeclaringType != typeof(object) && toString.DeclaringType != typeof(ValueType),
            $"{type} does not override ToString");
        Assert.True(ReturnsOnlyTheConstant(toString), $"{type}'s ToString ({toString.DeclaringType}) does more than return \"{Redacted}\"");
        // A wrong IL read cannot pass silently: the method must also answer the constant.
        Assert.Equal(Redacted, RuntimeHelpers.GetUninitializedObject(type).ToString());
    }

    private static bool ReturnsOnlyTheConstant(MethodInfo method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) return false;
        var strings = 0;
        for (var i = 0; i < il.Length;)
        {
            var op = il[i] == 0xFE ? (short)(0xFE00 | il[i + 1]) : il[i];
            if (!Allowed.Contains(op)) return false;
            i += il[i] == 0xFE ? 2 : 1;
            if (op == OpCodes.Ldstr.Value)
            {
                if (method.Module.ResolveString(BitConverter.ToInt32(il, i)) != Redacted) return false;
                strings++;
                i += 4;
            }
            else if (op == OpCodes.Br_S.Value)
                i += 1;
        }
        return strings == 1;
    }
}
