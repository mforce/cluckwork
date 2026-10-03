using System.Runtime.CompilerServices;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

internal static class QueryShapeWarningInitializer
{
    [ModuleInitializer]
    internal static void Initialize() =>
        Environment.SetEnvironmentVariable("Database__ThrowQueryShapeWarnings", "true");
}
