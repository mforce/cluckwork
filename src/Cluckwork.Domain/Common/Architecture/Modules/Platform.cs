namespace Cluckwork.Domain.Common.Architecture;

[ModuleOwner("Platform", "platform",
    Namespaces = [
        "Cluckwork.Domain.Common",
        "Cluckwork.Domain.Auditing",
        "Cluckwork.Application.Common",
        "Cluckwork.Infrastructure",
        "Cluckwork.Api",
        "Cluckwork.AppHost",
        "Cluckwork.Analyzers",
    ],
    ExactNamespaces = [
        "Cluckwork.Domain",
        "Cluckwork.Application",
    ])]
[AdapterRoots(
    Namespaces = [
        "Cluckwork.Api.Endpoints",
        "Cluckwork.Api.Modules",
        "Cluckwork.Api.Cli",
        "Cluckwork.Infrastructure.Jobs",
    ],
    Types = [
        "Cluckwork.Infrastructure.Persistence.DemoDataSeeder",
        "Cluckwork.Infrastructure.Persistence.SimulationDataSeeder",
    ],
    TopLevelPrograms = ["Cluckwork.Api"],
    PersistenceForbiddenNamespaces = ["Cluckwork.Api.Endpoints", "Cluckwork.Api.Modules"])]
[AdapterTier(
    "Cluckwork.Api.Mcp",
    "ContractOnly",
    "MapMcp",
    "MCP tool classes are adapters behind /mcp; CW1004 holds them to module contracts, as it holds endpoints (docs/decisions/806-mcp-endpoint.md)",
    "#806")]
internal static class PlatformModuleRules { }
