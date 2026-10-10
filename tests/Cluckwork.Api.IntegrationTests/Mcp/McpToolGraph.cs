using System.Reflection;
using Cluckwork.Api.Mcp;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// The reflection walks behind guards 6, 7 and 7b of docs/plans/770-mcp-server/02-guards.md.
// Tool types and methods are found by attribute NAME, as AdapterTierScanner does: the
// MCP SDK is not referenced yet (#806 adds it), and matching the simple name keeps the
// fixtures in McpToolGuardTests free of the SDK's namespace.
internal static class McpToolGraph
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    // Guard 7: a tool that injects any of these reads identity, or a scope, without the parse.
    private static readonly Type[] Ambient =
    [
        typeof(TenantContext), typeof(CurrentUserContext), typeof(ICurrentUser), typeof(FlockScope),
        typeof(AppDbContext), typeof(IServiceProvider), typeof(IServiceScopeFactory),
        typeof(HttpContext), typeof(IHttpContextAccessor),
    ];

    // Guard 7b: what opens or locates a scope. Ambient identity types are fine deeper in the
    // graph; resolved in the request scope, they ARE the request's.
    private static readonly Type[] ScopeOpeners =
        [typeof(IServiceProvider), typeof(IServiceScopeFactory), typeof(IServiceScope)];

    public static IReadOnlyList<Type> ToolTypes(Assembly assembly) =>
        [.. assembly.GetTypes().Where(t => HasAttribute(t, "McpServerToolTypeAttribute"))];

    // Guard 6. The SDK builds an instance tool type per call, so every constructor must take
    // the context; a static tool method has no instance and must take it as a parameter.
    public static IReadOnlyList<string> MissingCallContext(IEnumerable<Type> tools) =>
    [
        .. from tool in tools
           from method in ToolMethods(tool)
           where !Takes(method)
                 && (method.IsStatic || tool.GetConstructors().Any(c => !Takes(c)))
           select $"{tool.FullName}.{method.Name} can run without {nameof(McpCallContext)}",
    ];

    // Guard 7. Every constructor and every declared method, not only the tool methods:
    // the SDK binds method parameters from DI as well.
    public static IReadOnlyList<string> AmbientInjections(IEnumerable<Type> tools) =>
    [
        .. from tool in tools
           from member in Members(tool)
           from parameter in member.GetParameters()
           let hit = Ambient.FirstOrDefault(a => Mentions(parameter.ParameterType, a))
           where hit is not null
           select $"{tool.FullName}.{member.Name}({parameter.Name}) injects {hit.Name}",
    ];

    // Guard 7b. Walks dependencies through the ACTUAL registrations: an interface resolves to
    // what is registered for it, and an implementation type to its constructors. A factory
    // written in Cluckwork code is opaque (`sp => new Helper(() => sp.CreateScope())` shows
    // only a delegate), so one in reach is an escape unless its service type's full name is in
    // `reviewed`. Traversal stops at McpCallContext, at instances, at unregistered types (tool
    // arguments the SDK binds from JSON), and at framework code: implementations and factories
    // declared outside Cluckwork.
    public static IReadOnlyList<string> SecondaryScopeRoutes(
        IEnumerable<Type> tools, IServiceCollection services, IReadOnlySet<string> reviewed) =>
        Walk(
            from tool in tools
            from member in Members(tool)
            from parameter in member.GetParameters()
            select (tool.Name, parameter.ParameterType),
            services, reviewed);

    public static IReadOnlyList<string> Walk(
        IEnumerable<(string Owner, Type Root)> roots, IServiceCollection services, IReadOnlySet<string> reviewed)
    {
        var escapes = new List<string>();
        var visited = new HashSet<Type>();

        foreach (var (owner, root) in roots)
            Visit(root, $"{owner} -> ");

        return escapes;

        void Visit(Type type, string path)
        {
            if (!visited.Add(type)) return;
            path += Display(type);

            if (ScopeOpeners.FirstOrDefault(o => o.IsAssignableFrom(type)) is { } opener)
            {
                escapes.Add($"{path} reaches {opener.Name}");
                return;
            }
            if (type == typeof(McpCallContext)) return;

            foreach (var argument in type.IsGenericType ? type.GetGenericArguments() : [])
                Visit(argument, path + " -> ");

            foreach (var descriptor in services.Where(d => d.ServiceType == type
                         || (type.IsConstructedGenericType && d.ServiceType == type.GetGenericTypeDefinition())))
            {
                if (Factory(descriptor) is { } factory)
                {
                    if (IsCluckwork(factory.Method.DeclaringType) && !reviewed.Contains(descriptor.ServiceType.FullName!))
                        escapes.Add($"{path} is registered through an unreviewed factory");
                    continue;
                }
                if (Implementation(descriptor) is not { } implementation) continue;
                if (implementation.IsGenericTypeDefinition && type.IsConstructedGenericType)
                    implementation = implementation.MakeGenericType(type.GetGenericArguments());
                if (!IsCluckwork(implementation)) continue;

                foreach (var parameter in implementation.GetConstructors().SelectMany(c => c.GetParameters()))
                    Visit(parameter.ParameterType, path + " -> ");
            }
        }
    }

    private static bool IsCluckwork(Type? type) =>
        type?.Namespace?.StartsWith("Cluckwork.", StringComparison.Ordinal) == true;

    // ImplementationFactory throws on a keyed descriptor, so branch on IsKeyedService.
    private static Delegate? Factory(ServiceDescriptor d) =>
        d.IsKeyedService ? d.KeyedImplementationFactory : d.ImplementationFactory;

    private static Type? Implementation(ServiceDescriptor d) =>
        d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType;

    private static IEnumerable<MethodBase> Members(Type tool) =>
        tool.GetConstructors().Cast<MethodBase>().Concat(tool.GetMethods(Declared));

    private static IEnumerable<MethodInfo> ToolMethods(Type tool) =>
        tool.GetMethods(Declared).Where(m => HasAttribute(m, "McpServerToolAttribute"));

    private static bool Takes(MethodBase member) =>
        member.GetParameters().Any(p => p.ParameterType == typeof(McpCallContext));

    private static bool HasAttribute(MemberInfo member, string name) =>
        member.GetCustomAttributes(false).Any(a => a.GetType().Name == name);

    // Assignability catches subtypes (IKeyedServiceProvider, a derived DbContext); the
    // generic recursion catches wrappers (Func<IServiceScopeFactory>, Lazy<AppDbContext>).
    private static bool Mentions(Type type, Type forbidden) =>
        forbidden.IsAssignableFrom(type)
        || (type.IsGenericType && type.GetGenericArguments().Any(a => Mentions(a, forbidden)))
        || (type.HasElementType && Mentions(type.GetElementType()!, forbidden));

    private static string Display(Type type) =>
        type.IsGenericType ? type.Name[..type.Name.IndexOf('`')] : type.Name;
}
