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
    "ContractsOnly",
    "MapMcp",
    "MCP tool classes are adapters like the endpoints: CW1004 and the reach guard hold them to module contracts (#1123, #806)",
    "#789")]
internal static class PlatformModuleRules { }
