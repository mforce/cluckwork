using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// #805 row 7b — walks a tool's dependencies through the service REGISTRATIONS, not
// its constructor alone. A helper registered as sp => new Helper(() => sp.CreateScope())
// shows only a delegate on its constructor; its registration is what is opaque.
//
// Traversal resolves each dependency the way the container does (the last unkeyed
// registration, every registration for IEnumerable<T>), recurses into the
// constructors of Cluckwork implementation types, and stops at a framework type, a
// pre-built instance or a reviewed factory. Any other factory is a finding, and so
// is any dependency on a scope opener.
//
// IHttpContextAccessor and HttpContext count as scope openers too, through
// HttpContext.RequestServices: a helper can call CreateScope() on it and read with an
// unresolved, so unrestricted, FlockScope. Each type that takes one needs a
// ReviewedRequestReader row, checked per consuming type rather than per dependency.
//
// A reviewed factory is keyed by its service type and the type whose code holds the
// delegate, so a second factory for the same service, registered elsewhere, is not
// excused by the first one's review.
//
// This bounds the hazard. A static service locator and detached background work add
// no edge, so no walk over registrations can see them.
internal sealed record ReviewedFactory(Type Service, Type RegisteredBy, string Reason);

internal sealed record ReviewedRequestReader(Type Consumer, string Reason);

internal sealed record SecondaryScopeReport(
    IReadOnlyList<string> Findings,
    IReadOnlySet<ReviewedFactory> FactoriesReached,
    IReadOnlySet<ReviewedRequestReader> ReadersReached);

internal static class SecondaryScopeWalk
{
    private static readonly Type[] ScopeOpeners = [typeof(IServiceProvider), typeof(IServiceScopeFactory)];
    private static readonly Type[] RequestReaders = [typeof(IHttpContextAccessor), typeof(HttpContext)];

    public static bool OpensAScope(Type type) => Reaches(ScopeOpeners, type);

    private static bool ReadsTheRequest(Type type) => Reaches(RequestReaders, type);

    private static bool Reaches(Type[] targets, Type type) =>
        targets.Any(target => target.IsAssignableFrom(type))
        || type.GetGenericArguments().Any(argument => Reaches(targets, argument))
        || (type.HasElementType && Reaches(targets, type.GetElementType()!));

    public static SecondaryScopeReport Walk(
        IReadOnlyCollection<ServiceDescriptor> services,
        IEnumerable<(string Root, Type Dependency)> roots,
        IReadOnlyCollection<ReviewedFactory> reviewedFactories,
        IReadOnlyCollection<ReviewedRequestReader> reviewedReaders)
    {
        var findings = new List<string>();
        var factoriesReached = new HashSet<ReviewedFactory>();
        var readersReached = new HashSet<ReviewedRequestReader>();
        var visited = new HashSet<Type>();
        var pending = new Queue<(Type Type, Type? Consumer, string Path)>(
            roots.Select(r => (r.Dependency, (Type?)null, $"{r.Root} -> {Name(r.Dependency)}")));

        // Edges are judged before the visited check, so every consumer of an opener is reported.
        while (pending.TryDequeue(out var item))
        {
            var (type, consumer, path) = item;
            if (OpensAScope(type))
            {
                findings.Add($"{path}: opens a scope");
                continue;
            }

            if (ReadsTheRequest(type))
            {
                var reader = reviewedReaders.FirstOrDefault(r => r.Consumer == consumer);
                if (reader is null)
                    findings.Add($"{path}: reaches HttpContext.RequestServices, which can open a scope; " +
                        "review the consumer and add a ReviewedRequestReader row");
                else
                    readersReached.Add(reader);
                continue;
            }

            if (!visited.Add(type))
                continue;

            var element = type.IsConstructedGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? type.GetGenericArguments()[0]
                : null;
            var registrations = Registrations(services, element ?? type);
            if (element is null && registrations.Count > 1)
                registrations = [registrations[^1]];

            if (registrations.Count == 0 && IsCluckwork(type))
                findings.Add($"{path}: no registration, so the walk cannot see what it resolves to");

            foreach (var registration in registrations)
            {
                if (registration.ImplementationFactory is { } factory)
                {
                    var registeredBy = Outermost(factory.Method.DeclaringType!);
                    var review = reviewedFactories.FirstOrDefault(r =>
                        r.Service == registration.ServiceType && r.RegisteredBy == registeredBy);
                    if (review is null)
                        findings.Add($"{path}: registered through a factory delegate in {registeredBy.FullName}, " +
                            "which the walk cannot see into; review it and add a ReviewedFactory row");
                    else
                        factoriesReached.Add(review);
                    continue;
                }

                if (registration.ImplementationType is not { } implementation || !IsCluckwork(implementation))
                    continue;
                if (implementation.IsGenericTypeDefinition)
                    implementation = implementation.MakeGenericType((element ?? type).GetGenericArguments());

                foreach (var parameter in implementation.GetConstructors().SelectMany(c => c.GetParameters()))
                    pending.Enqueue((parameter.ParameterType, implementation, $"{path} -> {Name(parameter.ParameterType)}"));
            }
        }

        return new(findings, factoriesReached, readersReached);
    }

    private static Type Outermost(Type type) => type.DeclaringType is { } outer ? Outermost(outer) : type;

    private static List<ServiceDescriptor> Registrations(IEnumerable<ServiceDescriptor> services, Type type) =>
        services.Where(d => !d.IsKeyedService
            && (d.ServiceType == type
                || (type.IsConstructedGenericType && d.ServiceType == type.GetGenericTypeDefinition())))
            .ToList();

    private static bool IsCluckwork(Type type) =>
        type.Assembly.GetName().Name?.StartsWith("Cluckwork.", StringComparison.Ordinal) == true;

    private static string Name(Type type) =>
        type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(Name))}>"
            : type.Name;
}
