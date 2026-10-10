using System.Reflection;
using Cluckwork.Api.Mcp;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// #805 rows 6 and 7 — what a tool may inject. A tool is a type carrying an attribute
// NAMED McpServerToolTypeAttribute, and a tool method one carrying McpServerToolAttribute.
// Matched by name, as AdapterTierScanner matches it, so the fixtures here can declare
// look-alikes to prove the walk goes red.
internal static class McpToolSurface
{
    // A supertype is banned too: ICurrentUser is CurrentUserContext's port and DbContext
    // is AppDbContext's base, and either reaches the same scoped instance.
    public static readonly Type[] Banned =
    [
        typeof(TenantContext), typeof(CurrentUserContext), typeof(FlockScope), typeof(AppDbContext),
        typeof(IServiceProvider), typeof(IServiceScopeFactory), typeof(HttpContext), typeof(IHttpContextAccessor),
    ];

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static IEnumerable<Type> ToolTypes(IEnumerable<Type> types) =>
        types.Where(t => HasAttribute(t, "McpServerToolTypeAttribute"));

    public static IEnumerable<MethodInfo> ToolMethods(Type tool) =>
        tool.GetMethods(Declared).Where(m => HasAttribute(m, "McpServerToolAttribute"));

    // Everything DI hands a tool: constructor parameters and tool-method parameters.
    public static IEnumerable<(string Site, Type Type)> Injections(Type tool) =>
        tool.GetConstructors().SelectMany(c => c.GetParameters())
            .Select(p => ($"{tool.Name}({p.Name})", p.ParameterType))
            .Concat(ToolMethods(tool).SelectMany(m => m.GetParameters()
                .Select(p => ($"{tool.Name}.{m.Name}({p.Name})", p.ParameterType))));

    public static IReadOnlyList<string> Findings(IEnumerable<Type> types)
    {
        var findings = new List<string>();
        foreach (var tool in ToolTypes(types))
        {
            var constructorsTakeIt = tool.GetConstructors().Length > 0
                && tool.GetConstructors().All(c => c.GetParameters().Any(p => p.ParameterType == typeof(McpCallContext)));
            foreach (var method in ToolMethods(tool))
            {
                var takesIt = method.GetParameters().Any(p => p.ParameterType == typeof(McpCallContext))
                    || (!method.IsStatic && constructorsTakeIt);
                if (!takesIt)
                    findings.Add($"{tool.FullName}.{method.Name} runs without McpCallContext");
            }

            findings.AddRange(Injections(tool).Where(i => IsBanned(i.Type))
                .Select(i => $"{tool.FullName}: {i.Site} injects {i.Type.Name}; take McpCallContext instead"));
        }

        return findings;
    }

    // #806 — every tool method names a scope policy and a role policy, on itself or its
    // type. The SDK reads [Authorize] from both and ANDs them, so the tool admits role ∩
    // scope; a bare [Authorize] is the default policy, which is a role policy.
    public static IReadOnlyList<string> AuthorizationFindings(IEnumerable<Type> types) =>
        ToolTypes(types).SelectMany(tool => ToolMethods(tool).SelectMany(method =>
        {
            var policies = tool.GetCustomAttributes(inherit: true).Concat(method.GetCustomAttributes(inherit: true))
                .OfType<IAuthorizeData>().Select(a => a.Policy).ToList();
            var findings = new List<string>();
            if (!policies.Any(p => p is not null && OAuthScopes.All.Contains(p)))
                findings.Add($"{tool.FullName}.{method.Name} names no scope policy");
            if (!policies.Any(p => p is null || !OAuthScopes.All.Contains(p)))
                findings.Add($"{tool.FullName}.{method.Name} names no role policy");
            return findings;
        })).ToList();

    // #805 row 7b, the namespace half: no type under the MCP namespace declares a scope
    // opener, as a constructor or method parameter, return type, field or property. A
    // lambda taking IServiceProvider compiles to a method on a nested type, so it counts.
    public static IReadOnlyList<string> ScopeOpenerDeclarations(IEnumerable<Type> types) =>
        types.SelectMany(type =>
            type.GetConstructors(Declared).SelectMany(c => c.GetParameters())
                .Select(p => (Member: $".ctor({p.Name})", Type: p.ParameterType))
                .Concat(type.GetMethods(Declared).SelectMany(m => m.GetParameters()
                    .Select(p => (Member: $"{m.Name}({p.Name})", Type: p.ParameterType))
                    .Append((Member: $"{m.Name} returns", Type: m.ReturnType))))
                .Concat(type.GetFields(Declared).Select(f => (Member: f.Name, Type: f.FieldType)))
                .Concat(type.GetProperties(Declared).Select(p => (Member: p.Name, Type: p.PropertyType)))
                .Where(d => SecondaryScopeWalk.OpensAScope(d.Type))
                .Select(d => $"{type.FullName}.{d.Member} declares {d.Type.Name}"))
            .ToList();

    public static bool InNamespace(Type type, string root) =>
        type.Namespace == root || type.Namespace?.StartsWith(root + ".", StringComparison.Ordinal) == true;

    private static bool IsBanned(Type type) =>
        Banned.Any(banned => banned.IsAssignableFrom(type) || (type != typeof(object) && type.IsAssignableFrom(banned)))
        || type.GetGenericArguments().Any(IsBanned)
        || (type.HasElementType && IsBanned(type.GetElementType()!));

    private static bool HasAttribute(MemberInfo member, string name) =>
        member.CustomAttributes.Any(a => a.AttributeType.Name == name);
}
