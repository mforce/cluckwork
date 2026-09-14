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
        (t => typeof(IQueryable).IsAssignableFrom(t), "an IQueryable"),
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
            .Where(t => t.IsInterface && IsPubliclyReachable(t) && MatchesPrefix(t.Namespace, namespacePrefixes))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        var violations = new List<SeamSurfaceViolation>();
        foreach (var iface in interfaces)
        {
            if (iface.IsGenericTypeDefinition)
            {
                foreach (var parameter in iface.GetGenericArguments())
                {
                    Walk(parameter, [FormatShort(parameter)], new HashSet<Type>(), iface.FullName!, $"<{parameter.Name}>", violations);
                }
            }

            CheckMembers(iface, iface.FullName!, violations);

            // Reflection does not surface inherited interface members; the
            // constructed base interfaces carry them with substituted arguments.
            foreach (var baseInterface in iface.GetInterfaces())
            {
                Walk(baseInterface, [FormatShort(baseInterface)], new HashSet<Type>(), iface.FullName!, $": {FormatShort(baseInterface)}", violations);
                CheckMembers(baseInterface, iface.FullName!, violations);
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

    private static void CheckMembers(Type declaring, string interfaceName, List<SeamSurfaceViolation> violations)
    {
        void Check(Type type, string member) =>
            Walk(type, [FormatShort(type)], new HashSet<Type>(), interfaceName, member, violations);

        // Only accessors are skipped; their property or event is walked below.
        // Operators are special-name too and must be walked.
        var accessors = declaring.GetProperties().SelectMany(p => p.GetAccessors())
            .Concat(declaring.GetEvents().SelectMany(e => new[] { e.AddMethod, e.RemoveMethod }.OfType<MethodInfo>()))
            .ToHashSet();
        foreach (var method in declaring.GetMethods().Where(m => !accessors.Contains(m)))
        {
            CheckMethod(method, interfaceName, violations);
        }

        foreach (var property in declaring.GetProperties())
        {
            Check(property.PropertyType, property.Name);
            foreach (var index in property.GetIndexParameters())
            {
                Check(index.ParameterType, property.Name);
            }
        }

        foreach (var evt in declaring.GetEvents())
        {
            if (evt.EventHandlerType is { } handler)
            {
                Check(handler, evt.Name);
            }
        }
    }

    private static void CheckMethod(MethodInfo method, string interfaceName, List<SeamSurfaceViolation> violations)
    {
        void Check(Type type) =>
            Walk(type, [FormatShort(type)], new HashSet<Type>(), interfaceName, method.Name, violations);

        foreach (var parameter in method.GetParameters())
        {
            Check(parameter.ParameterType);
        }

        if (method.ReturnType != typeof(void))
        {
            Check(method.ReturnType);
        }
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
        if (!visited.Add(resolved))
        {
            return;
        }

        if (resolved.IsGenericParameter)
        {
            foreach (var constraint in resolved.GetGenericParameterConstraints())
            {
                Walk(constraint, [.. path, FormatShort(constraint)], visited, interfaceName, member, violations);
            }

            return;
        }

        var rule = ForbiddenRules.FirstOrDefault(r => r.Matches(resolved));
        if (rule.Reason is not null)
        {
            violations.Add(new SeamSurfaceViolation(
                interfaceName, member, resolved.FullName ?? resolved.Name, string.Join(" -> ", path)));
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

        if (resolved.IsFunctionPointer)
        {
            foreach (var parameter in resolved.GetFunctionPointerParameterTypes())
            {
                Walk(parameter, [.. path, FormatShort(parameter)], visited, interfaceName, member, violations);
            }

            Walk(resolved.GetFunctionPointerReturnType(), [.. path, FormatShort(resolved.GetFunctionPointerReturnType())],
                visited, interfaceName, member, violations);
            return;
        }

        if (typeof(Delegate).IsAssignableFrom(resolved) && resolved.GetMethod("Invoke") is { } invoke)
        {
            foreach (var parameter in invoke.GetParameters())
            {
                Walk(parameter.ParameterType, [.. path, FormatShort(parameter.ParameterType)], visited,
                    interfaceName, member, violations);
            }

            if (invoke.ReturnType != typeof(void))
            {
                Walk(invoke.ReturnType, [.. path, FormatShort(invoke.ReturnType)], visited,
                    interfaceName, member, violations);
            }
        }

        // Properties are followed once per generic definition: a Cluckwork type
        // whose property re-expands its own type argument would otherwise mint a
        // new constructed type at every step and never terminate.
        var assemblyName = resolved.Assembly.GetName().Name;
        var definition = resolved.IsGenericType ? resolved.GetGenericTypeDefinition() : resolved;
        if (assemblyName is not null && assemblyName.StartsWith("Cluckwork.", StringComparison.Ordinal)
            && (definition == resolved || visited.Add(definition)))
        {
            foreach (var property in resolved.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Walk(property.PropertyType, [.. path, $"{FormatShort(resolved)}.{property.Name}"], visited,
                    interfaceName, member, violations);
            }
        }
    }

    private static bool IsPubliclyReachable(Type type) =>
        type.IsPublic || (type.IsNestedPublic && IsPubliclyReachable(type.DeclaringType!));

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
