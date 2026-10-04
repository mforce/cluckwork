namespace Cluckwork.Application.Tests.Architecture;

internal static partial class RealModuleLedger
{
    internal static readonly AdapterRoots AdapterRoots = new(
        [
            "Cluckwork.Api.Endpoints",
            "Cluckwork.Api.Cli",
            "Cluckwork.Infrastructure.Jobs",
        ],
        [
            "Cluckwork.Infrastructure.Persistence.DemoDataSeeder",
            "Cluckwork.Infrastructure.Persistence.SimulationDataSeeder",
        ])
    {
        TopLevelPrograms = ["Cluckwork.Api"],
        PersistenceForbiddenNamespaces = ["Cluckwork.Api.Endpoints"],
    };

    internal static readonly AdapterTier[] AdapterTiers =
    [
        new("Cluckwork.Api.Mcp",
            "DirectRepository",
            "MapMcp",
            "MCP tool classes inject repositories by design (docs/plans/770-mcp-server/01-design.md:112); the contract-first shape is Track C (#514)",
            "#806"),
    ];
}
