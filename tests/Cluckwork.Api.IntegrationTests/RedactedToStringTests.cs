using System.Reflection;
using System.Reflection.Emit;
using Cluckwork.Api.IntegrationTests.Infrastructure;

namespace Cluckwork.Api.IntegrationTests;

// G3.29's own guard: the IL scanner accepts a constant ToString and refuses
// every other shape, so a weakened scanner cannot pass the walks silently.
public sealed class RedactedToStringTests
{
    private sealed record Accepted(string Password)
    {
        public override string ToString() => "[redacted]";
    }

    private sealed record HelperCall(string Password)
    {
        private string Reveal() => Password;
        public override string ToString() => Reveal();
    }

    private sealed record Concat(string Password)
    {
        public override string ToString() => "[redacted]" + Password;
    }

    private sealed class FieldLoad
    {
        private readonly string _shown = "[redacted]";
        public override string ToString() => _shown;
    }

    private sealed record WrongLiteral(string Password)
    {
        public override string ToString() => "[hidden]";
    }

    private sealed record StateDependent(string? Password)
    {
        public override string ToString() => Password is null ? "[redacted]" : Password;
    }

    private sealed record Formats(string Password) : IFormattable
    {
        public override string ToString() => "[redacted]";
        public string ToString(string? format, IFormatProvider? formatProvider) => Password;
    }

    // Convert.ToString and some formatters reach IConvertible.ToString(IFormatProvider).
    private sealed record Converts(string Password) : IConvertible
    {
        public override string ToString() => "[redacted]";
        public string ToString(IFormatProvider? provider) => Password;
        public TypeCode GetTypeCode() => TypeCode.Object;
        public bool ToBoolean(IFormatProvider? provider) => throw new NotSupportedException();
        public byte ToByte(IFormatProvider? provider) => throw new NotSupportedException();
        public char ToChar(IFormatProvider? provider) => throw new NotSupportedException();
        public DateTime ToDateTime(IFormatProvider? provider) => throw new NotSupportedException();
        public decimal ToDecimal(IFormatProvider? provider) => throw new NotSupportedException();
        public double ToDouble(IFormatProvider? provider) => throw new NotSupportedException();
        public short ToInt16(IFormatProvider? provider) => throw new NotSupportedException();
        public int ToInt32(IFormatProvider? provider) => throw new NotSupportedException();
        public long ToInt64(IFormatProvider? provider) => throw new NotSupportedException();
        public sbyte ToSByte(IFormatProvider? provider) => throw new NotSupportedException();
        public float ToSingle(IFormatProvider? provider) => throw new NotSupportedException();
        public object ToType(Type conversionType, IFormatProvider? provider) => throw new NotSupportedException();
        public ushort ToUInt16(IFormatProvider? provider) => throw new NotSupportedException();
        public uint ToUInt32(IFormatProvider? provider) => throw new NotSupportedException();
        public ulong ToUInt64(IFormatProvider? provider) => throw new NotSupportedException();
    }

    // Logger and interpolation paths that write UTF-8 call TryFormat.
    private sealed record FormatsUtf8(string Password) : IUtf8SpanFormattable
    {
        public override string ToString() => "[redacted]";
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
            System.Text.Encoding.UTF8.TryGetBytes(Password, utf8Destination, out bytesWritten);
    }

    private static (byte[] Il, Module Module) IlOf(Type type)
    {
        var toString = type.GetMethod(nameof(ToString), BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!;
        return (toString.GetMethodBody()!.GetILAsByteArray()!, toString.Module);
    }

    [Fact]
    public void TheScanner_AcceptsAConstantToString()
    {
        var (il, module) = IlOf(typeof(Accepted));

        Assert.True(RedactedToString.ReturnsOnlyTheConstant(il, module));
    }

    [Theory]
    [InlineData(typeof(HelperCall))]
    [InlineData(typeof(Concat))]
    [InlineData(typeof(FieldLoad))]
    [InlineData(typeof(WrongLiteral))]
    [InlineData(typeof(StateDependent))]
    public void TheScanner_RefusesEveryOtherToString(Type type)
    {
        var (il, module) = IlOf(type);

        Assert.False(RedactedToString.ReturnsOnlyTheConstant(il, module), $"{type.Name} passed the scanner");
    }

    // An uninitialized StateDependent prints "[redacted]", so only the IL
    // check inside AssertConstant can refuse it.
    [Fact]
    public void AssertConstant_RefusesAToStringThatPrintsOnlyWhenSet()
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => RedactedToString.AssertConstant(typeof(StateDependent)));
    }

    // Its ToString is the constant, but interpolation calls the format method.
    [Fact]
    public void AssertConstant_RefusesATypeThatFormatsItself()
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => RedactedToString.AssertConstant(typeof(Formats)));
    }

    // Its ToString is the constant, but a UTF-8 writer calls TryFormat.
    [Fact]
    public void AssertConstant_RefusesATypeThatFormatsItselfAsUtf8()
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => RedactedToString.AssertConstant(typeof(FormatsUtf8)));
    }

    // Its ToString is the constant, but Convert.ToString calls the IConvertible one.
    [Fact]
    public void AssertConstant_RefusesATypeThatConvertsItself()
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => RedactedToString.AssertConstant(typeof(Converts)));
    }

    // A br.s whose operand is not 0: read as an op code, the operand byte is
    // break, so the scanner accepts this only when it skips the operand.
    [Fact]
    public void TheScanner_SkipsABranchOperand()
    {
        var (il, module) = IlOf(typeof(Accepted));
        var ldstr = Array.IndexOf(il, (byte)OpCodes.Ldstr.Value);
        byte[] branch = [(byte)OpCodes.Br_S.Value, 1, (byte)OpCodes.Nop.Value];

        Assert.True(RedactedToString.ReturnsOnlyTheConstant([.. il[ldstr..(ldstr + 5)], .. branch, (byte)OpCodes.Ret.Value], module));
    }

    // No C# body compiles to two ldstr with nothing between them, so this one
    // is built from the accepted body's own string token.
    [Fact]
    public void TheScanner_RefusesTwoConstants()
    {
        var (il, module) = IlOf(typeof(Accepted));
        var ldstr = Array.IndexOf(il, (byte)OpCodes.Ldstr.Value);
        var load = il[ldstr..(ldstr + 5)];

        Assert.False(RedactedToString.ReturnsOnlyTheConstant([.. load, .. load, (byte)OpCodes.Ret.Value], module));
    }

    // A RecordHit outside Coverlet's tracker namespace.
    private static class OtherTracker
    {
        public static void RecordHit(int id) => _ = id;
    }

    // The accepted body with a coverage-counter pair in front of it: the given
    // constant load, then a call to the given method.
    private static byte[] WithCounter(byte[] load, MethodInfo tracker)
    {
        var (il, _) = IlOf(typeof(Accepted));
        var ldstr = Array.IndexOf(il, (byte)OpCodes.Ldstr.Value);
        return [.. load, (byte)OpCodes.Call.Value, .. BitConverter.GetBytes(tracker.MetadataToken),
            .. il[ldstr..(ldstr + 5)], (byte)OpCodes.Ret.Value];
    }

    private static MethodInfo RecordHitOf(Type tracker) =>
        tracker.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Single();

    public static TheoryData<string> CounterLoads() => ["ldc.i4", "ldc.i4.s", "ldc.i4.0", "ldc.i4.6", "ldc.i4.8", "ldc.i4.m1"];

    private static byte[] Load(string form) => form switch
    {
        "ldc.i4" => [(byte)OpCodes.Ldc_I4.Value, 7, 0, 0, 0],
        "ldc.i4.s" => [(byte)OpCodes.Ldc_I4_S.Value, 7],
        "ldc.i4.0" => [(byte)OpCodes.Ldc_I4_0.Value],
        "ldc.i4.6" => [(byte)OpCodes.Ldc_I4_6.Value],
        "ldc.i4.8" => [(byte)OpCodes.Ldc_I4_8.Value],
        "ldc.i4.m1" => [(byte)OpCodes.Ldc_I4_M1.Value],
        _ => throw new ArgumentOutOfRangeException(nameof(form)),
    };

    // Coverlet loads its counter id with whichever ldc.i4 form fits.
    [Theory]
    [MemberData(nameof(CounterLoads))]
    public void TheScanner_AcceptsTheCoverageCounter_InEveryLoadForm(string form)
    {
        var il = WithCounter(Load(form), RecordHitOf(typeof(Coverlet.Core.Instrumentation.Tracker.CluckworkFixture.Counter)));

        Assert.True(RedactedToString.ReturnsOnlyTheConstant(il, typeof(RedactedToStringTests).Module));
    }

    // Each condition of the counter allowance, failed alone.
    public static TheoryData<string> CounterConditions() => ["name", "namespace", "static", "void", "one int"];

    [Theory]
    [MemberData(nameof(CounterConditions))]
    public void TheScanner_RefusesACounterThatFailsOneCondition(string condition)
    {
        var tracker = condition switch
        {
            "name" => typeof(Coverlet.Core.Instrumentation.Tracker.CluckworkFixture.WrongName),
            "namespace" => typeof(OtherTracker),
            "static" => typeof(Coverlet.Core.Instrumentation.Tracker.CluckworkFixture.Instance),
            "void" => typeof(Coverlet.Core.Instrumentation.Tracker.CluckworkFixture.ReturnsValue),
            "one int" => typeof(Coverlet.Core.Instrumentation.Tracker.CluckworkFixture.TakesLong),
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };
        var il = WithCounter(Load("ldc.i4.s"), RecordHitOf(tracker));

        Assert.False(RedactedToString.ReturnsOnlyTheConstant(il, typeof(RedactedToStringTests).Module), $"a counter with the wrong {condition} passed");
    }

    // The tracker must belong to the assembly whose ToString is scanned.
    [Fact]
    public void TheScanner_RefusesACounterFromAnotherAssembly()
    {
        var il = WithCounter(Load("ldc.i4.s"), RecordHitOf(typeof(Coverlet.Core.Instrumentation.Tracker.CluckworkFixture.Counter)));

        Assert.False(RedactedToString.ReturnsOnlyTheConstant(il, typeof(RedactedToStringTests).Module, owner: typeof(object).Assembly));
    }

    private static void NotACounter(int id) => _ = id;

    // Coverlet's hit counter is ldc.i4 then a call to its tracker's RecordHit;
    // the same pair aimed at any other method is a call, and is refused.
    [Fact]
    public void TheScanner_RefusesACallShapedLikeTheCoverageCounter()
    {
        var (il, module) = IlOf(typeof(Accepted));
        var ldstr = Array.IndexOf(il, (byte)OpCodes.Ldstr.Value);
        var other = BitConverter.GetBytes(typeof(RedactedToStringTests)
            .GetMethod(nameof(NotACounter), BindingFlags.NonPublic | BindingFlags.Static)!.MetadataToken);
        byte[] counter = [(byte)OpCodes.Ldc_I4.Value, 0, 0, 0, 0, (byte)OpCodes.Call.Value, .. other];

        Assert.False(RedactedToString.ReturnsOnlyTheConstant([.. counter, .. il[ldstr..(ldstr + 5)], (byte)OpCodes.Ret.Value], module));
    }
}
