using Cluckwork.Api.IntegrationTests;

// The runtime looks for exactly this global type when DOTNET_STARTUP_HOOKS
// names this assembly; FixturePortRegistrationTests is the only caller.
internal static class StartupHook
{
    public static void Initialize() => FixturePortProbe.Start();
}
