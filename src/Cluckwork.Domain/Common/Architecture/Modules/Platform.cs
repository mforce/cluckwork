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
internal static class PlatformModuleRules { }
