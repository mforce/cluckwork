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
    "DirectRepository",
    "MapMcp",
    "MCP tool classes inject repositories by design (docs/plans/770-mcp-server/01-design.md:112); the contract-first shape is Track C (#514)",
    "#806")]
internal static class PlatformModuleRules { }
