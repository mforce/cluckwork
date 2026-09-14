// #847 (epic #514 slice 5) — no persistence type crosses a module seam.

namespace Cluckwork.Application.Tests.Architecture;

using System.Reflection;
using Cluckwork.Domain.Common;
using Microsoft.EntityFrameworkCore;

public sealed record SeamSurfaceViolation(
    string Interface,
    string Member,
    string OffendingType,
    string Path);

public sealed record SeamSurfaceReport(
    IReadOnlyList<string> InspectedInterfaces,
    int ExpectedInterfaceFloor,
    IReadOnlyList<SeamSurfaceViolation> Violations);

public static class SeamSurfaceScanner
{
    private const string EfNamespace = "Microsoft.EntityFrameworkCore";
    private const string InfrastructureNamespace = "Cluckwork.Infrastructure";

    // Checked in order; the first match wins. Kept as a small table rather than
    // scattered ifs so a new forbidden shape is one row, not a new branch.
    private static readonly (Func<Type, bool> Matches, string Reason)[] ForbiddenRules =
    [
        (t => typeof(DbContext).IsAssignableFrom(t), "a DbContext"),
        (t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(DbSet<>), "a DbSet<>"),
        (t => t == typeof(IQueryable)
              || (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(IQueryable<>)
                                       || t.GetGenericTypeDefinition() == typeof(IOrderedQueryable<>))),
            "an IQueryable"),
        (t => InNamespace(t, EfNamespace), "in Microsoft.EntityFrameworkCore"),
        (t => t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(Entity<>)
                                   || t.GetGenericTypeDefinition() == typeof(AggregateRoot<>)),
            "a domain aggregate base type (Entity<> or AggregateRoot<>) rather than a concrete aggregate"),
        (t => InNamespace(t, InfrastructureNamespace), "in Cluckwork.Infrastructure"),
    ];

    // The caller states the interface count it expects, so a namespace-prefix
    // typo that matches nothing reds instead of passing on zero.
    public static SeamSurfaceReport Scan(
        Assembly assembly, IReadOnlyList<string> namespacePrefixes, int minimumInterfaceFloor)
    {
        var interfaces = assembly.GetTypes()
            .Where(t => t.IsInterface && t.IsPublic && MatchesPrefix(t.Namespace, namespacePrefixes))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        var violations = new List<SeamSurfaceViolation>();
        foreach (var iface in interfaces)
        {
            foreach (var method in iface.GetMethods().Where(m => !m.IsSpecialName))
            {
                foreach (var parameter in method.GetParameters())
                {
                    Walk(parameter.ParameterType, [FormatShort(parameter.ParameterType)], new HashSet<Type>(),
                        iface.FullName!, method.Name, violations);
                }

                if (method.ReturnType != typeof(void))
                {
                    Walk(method.ReturnType, [FormatShort(method.ReturnType)], new HashSet<Type>(),
                        iface.FullName!, method.Name, violations);
                }
            }

            foreach (var property in iface.GetProperties())
            {
                Walk(property.PropertyType, [FormatShort(property.PropertyType)], new HashSet<Type>(),
                    iface.FullName!, property.Name, violations);
            }
        }

        return new SeamSurfaceReport(
            interfaces.Select(i => i.FullName!).ToList(), minimumInterfaceFloor, violations);
    }

    // Application references no EF, Npgsql or Infrastructure assembly; that is
    // what keeps the DbSet/DbContext rules above from being vacuous.
    public static IReadOnlyList<string> EvaluateReferences(Assembly assembly)
    {
        var failures = new List<string>();
        foreach (var reference in assembly.GetReferencedAssemblies())
        {
            if (reference.Name is null)
            {
                continue;
            }

            if (reference.Name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || reference.Name.StartsWith("Npgsql", StringComparison.Ordinal)
                || reference.Name == "Cluckwork.Infrastructure")
            {
                failures.Add(
                    $"{assembly.GetName().Name} references {reference.Name} — Application must stay free of " +
                    "EF/Npgsql/Infrastructure so the reflection walk's DbSet/DbContext rules are not vacuous");
            }
        }

        return failures;
    }

    /// <summary>Evaluates a report as a build gate: the interface floor holds and no violation was found. Returns the failure messages (empty = pass).</summary>
    public static IReadOnlyList<string> Evaluate(SeamSurfaceReport report)
    {
        var failures = new List<string>();

        if (report.InspectedInterfaces.Count < report.ExpectedInterfaceFloor)
        {
            failures.Add(
                $"inspected {report.InspectedInterfaces.Count} interface(s), expected at least " +
                $"{report.ExpectedInterfaceFloor} — a namespace-prefix typo must red, not pass on zero");
        }

        foreach (var violation in report.Violations)
        {
            failures.Add(
                $"{violation.Interface}.{violation.Member} exposes {violation.OffendingType} via {violation.Path}");
        }

        return failures;
    }

    private static void Walk(
        Type type,
        List<string> path,
        HashSet<Type> visited,
        string interfaceName,
        string member,
        List<SeamSurfaceViolation> violations)
    {
        var resolved = type.IsByRef ? type.GetElementType()! : type;

        var rule = ForbiddenRules.FirstOrDefault(r => r.Matches(resolved));
        if (rule.Reason is not null)
        {
            violations.Add(new SeamSurfaceViolation(
                interfaceName, member, resolved.FullName ?? resolved.Name, string.Join(" -> ", path)));
            return;
        }

        if (!visited.Add(resolved))
        {
            return;
        }

        if (resolved.IsArray)
        {
            var elementType = resolved.GetElementType()!;
            Walk(elementType, [.. path, FormatShort(elementType)], visited, interfaceName, member, violations);
            return;
        }

        if (resolved.IsGenericType)
        {
            foreach (var argument in resolved.GetGenericArguments())
            {
                Walk(argument, [.. path, FormatShort(argument)], visited, interfaceName, member, violations);
            }
        }

        var assemblyName = resolved.Assembly.GetName().Name;
        if (assemblyName is not null && assemblyName.StartsWith("Cluckwork.", StringComparison.Ordinal))
        {
            foreach (var property in resolved.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Walk(property.PropertyType, [.. path, $"{FormatShort(resolved)}.{property.Name}"], visited,
                    interfaceName, member, violations);
            }
        }
    }

    private static bool InNamespace(Type type, string prefix) =>
        type.Namespace is string ns && (ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal));

    private static bool MatchesPrefix(string? ns, IReadOnlyList<string> prefixes) =>
        ns is not null && prefixes.Any(p => ns == p || ns.StartsWith(p + ".", StringComparison.Ordinal));

    private static string FormatShort(Type type)
    {
        if (type.IsByRef)
        {
            return FormatShort(type.GetElementType()!);
        }

        if (type.IsArray)
        {
            return $"{FormatShort(type.GetElementType()!)}[]";
        }

        if (type.IsGenericType)
        {
            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick > 0)
            {
                name = name[..tick];
            }

            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FormatShort))}>";
        }

        return type.Name;
    }
}
