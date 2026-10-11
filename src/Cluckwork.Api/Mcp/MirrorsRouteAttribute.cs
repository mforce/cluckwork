namespace Cluckwork.Api.Mcp;

// #807 and #810, guard rows 13 to 15 — the REST routes a tool answers the same question as,
// by their WithName. A tool may admit no role any of its routes denies; McpRouteParityTests
// holds it there, so tightening one route alone turns the test red.
[AttributeUsage(AttributeTargets.Method)]
public sealed class MirrorsRouteAttribute(string routeName, params string[] moreRouteNames) : Attribute
{
    public IReadOnlyList<string> RouteNames { get; } = [routeName, .. moreRouteNames];
}
