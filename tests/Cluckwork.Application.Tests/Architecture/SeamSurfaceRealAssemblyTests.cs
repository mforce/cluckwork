// #847 — the real-assembly gate; the mutation matrix reds here.

namespace Cluckwork.Application.Tests.Architecture;

using System.Reflection;
using Cluckwork.Application.Common;

public sealed class SeamSurfaceRealAssemblyTests
{
    private static Assembly ApplicationAssembly => typeof(IRepository<,>).Assembly;

    [Fact]
    public void RealApplicationAssembly_NoPublicInterfaceExposesPersistence()
    {
        var report = SeamSurfaceScanner.Scan(
            ApplicationAssembly,
            ["Cluckwork.Application.Features", "Cluckwork.Application.Common"],
            minimumInterfaceFloor: 30);

        var failures = SeamSurfaceScanner.Evaluate(report);
        Assert.True(failures.Count == 0, "seam-surface guard failed:\n  " + string.Join("\n  ", failures));
        Assert.True(report.InspectedInterfaces.Count >= 30,
            $"inspected only {report.InspectedInterfaces.Count} interfaces — expected at least 30");
    }

    [Fact]
    public void RealApplicationAssembly_DoesNotReferenceEntityFrameworkOrInfrastructure()
    {
        var failures = SeamSurfaceScanner.EvaluateReferences(ApplicationAssembly);
        Assert.True(failures.Count == 0, "assembly-reference pin failed:\n  " + string.Join("\n  ", failures));
    }
}
