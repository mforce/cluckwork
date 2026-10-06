using Cluckwork.Application.Modules.Access.Contracts;
using System.Diagnostics;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cluckwork.Api.IntegrationTests;

// #858 — a fixture port is a module contract type, so the ledger lets any
// adapter inject it. What keeps its cross-farm-shaped reads and its
// step-up-free flock assignment out of request code is that only a
// non-Production host registers it. Each case runs the real Cluckwork.Api.dll,
// as a server and as a one-shot verb, in its own process whose environment
// selectors are exactly what the case says, and resolves every fixture port
// from the built container (FixturePortProbe, below, as a startup hook).
public sealed class FixturePortRegistrationTests
{
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public void ConventionFindsTheDeclaredPorts() =>
        Assert.True(FixturePortProbe.Ports.Length >= 7, string.Join(", ", FixturePortProbe.Ports.Select(t => t.Name)));

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Production", true)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("Testing", false)]
    [InlineData("Testing", true)]
    public async Task FixturePortsResolveOnlyOutsideProduction(string? environment, bool oneShot)
    {
        var (hostEnvironment, resolved) = await ProbeAsync(environment, oneShot);

        // A null environment is the operator who set nothing; the host must read it as Production.
        Assert.Equal(environment ?? Environments.Production, hostEnvironment);
        Assert.Equal(
            hostEnvironment == Environments.Production
                ? Array.Empty<string>()
                : FixturePortProbe.Ports.Select(port => port.Name).ToArray(),
            resolved);
    }

    private static async Task<(string Environment, string[] Resolved)> ProbeAsync(string? environment, bool oneShot)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(ApiDllPath);
        if (oneShot) psi.ArgumentList.Add("migrate");

        // The case decides the selectors, never the runner's shell.
        psi.Environment.Remove("ASPNETCORE_ENVIRONMENT");
        psi.Environment.Remove("DOTNET_ENVIRONMENT");
        if (environment is not null) psi.Environment["DOTNET_ENVIRONMENT"] = environment;

        psi.Environment["DOTNET_STARTUP_HOOKS"] = typeof(FixturePortProbe).Assembly.Location;
        psi.Environment[FixturePortProbe.Switch] = "1";
        psi.Environment["DOTNET_USE_POLLING_FILE_WATCHER"] = "1";
        psi.Environment["ConnectionStrings__Default"] =
            "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none";
        psi.Environment["Database__AllowInsecureConnection"] = "true";
        psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem;
        psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem;
        psi.Environment["RateLimiting__TrustedProxies__0"] = "10.0.0.0/8";

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var line = (await stdout).Split('\n').FirstOrDefault(l => l.StartsWith(FixturePortProbe.Marker, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The probe reported nothing (exit {process.ExitCode}).\nstdout={await stdout}\nstderr={await stderr}");
        var report = JsonSerializer.Deserialize<FixturePortProbe.Report>(line[FixturePortProbe.Marker.Length..])!;
        return (report.Environment, report.Resolved);
    }
}

// Loaded into the Api process through DOTNET_STARTUP_HOOKS. When the switch is
// set it stops Program.cs at HostBuilt, before the CLI dispatch or Kestrel, and
// prints which fixture ports the built container resolves.
internal sealed class FixturePortProbe : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    public const string Switch = "CLUCKWORK_FIXTURE_PORT_PROBE";
    public const string Marker = "FIXTURE-PORTS ";

    // Fixture ports are named I<Module>Fixture; Access's read port predates the convention.
    public static readonly Type[] Ports =
    [
        typeof(IAccessSeedLookup),
        .. typeof(IAccessSeedLookup).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.Name.StartsWith('I') && t.Name.EndsWith("Fixture", StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal),
    ];

    public sealed record Report(string Environment, string[] Resolved);

    public static void Start()
    {
        if (System.Environment.GetEnvironmentVariable(Switch) == "1")
            DiagnosticListener.AllListeners.Subscribe(new FixturePortProbe());
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "Microsoft.Extensions.Hosting")
            listener.Subscribe(this);
    }

    public void OnNext(KeyValuePair<string, object?> e)
    {
        if (e.Key != "HostBuilt") return;
        var host = (IHost)e.Value!;
        using var scope = host.Services.CreateScope();
        var report = new Report(
            host.Services.GetRequiredService<IHostEnvironment>().EnvironmentName,
            [.. Ports.Where(port => scope.ServiceProvider.GetService(port) is not null).Select(port => port.Name)]);
        Console.Out.WriteLine(Marker + JsonSerializer.Serialize(report));
        Console.Out.Flush();
        System.Environment.Exit(0);
    }

    public void OnCompleted() { }

    public void OnError(Exception error) { }
}
