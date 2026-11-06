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
