namespace Cluckwork.Api.Mcp;

// #807, guard rows 13 to 15 — the REST route a tool answers the same question as, by its
// WithName. A tool may admit no role its route denies; McpRouteParityTests holds it there,
// so tightening the route alone turns the test red.
[AttributeUsage(AttributeTargets.Method)]
public sealed class MirrorsRouteAttribute(string routeName) : Attribute
{
    public string RouteName { get; } = routeName;
}
