using System.Diagnostics;
using System.Reflection;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cluckwork.Api.IntegrationTests;

// #858 — a fixture port is a module contract type, so the ledger lets any
// adapter inject it. What keeps its cross-farm-shaped reads out of request code
// is that only a non-Production host registers it. This runs the real
// Program.cs up to HostBuilt, as a server and as a one-shot verb, and resolves
// every fixture port from the built container.
[Collection(IntegrationCollection.Name)]
public sealed class FixturePortRegistrationTests
{
    // Fixture ports are named I<Module>Fixture; Access's predates the convention.
    private static readonly Type[] FixturePorts =
    [
        typeof(IAccessSeedLookup),
        .. typeof(IAccessSeedLookup).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.Name.StartsWith('I') && t.Name.EndsWith("Fixture", StringComparison.Ordinal)),
    ];

    private static readonly AsyncLocal<bool> Capturing = new();

    [Fact]
    public void ConventionFindsTheDeclaredPorts() =>
        Assert.True(FixturePorts.Length >= 3, string.Join(", ", FixturePorts.Select(t => t.Name)));

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Production", true)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("Testing", false)]
    [InlineData("Testing", true)]
    public void FixturePortsResolveOnlyOutsideProduction(string? environment, bool oneShot)
    {
        using var host = BuildProgramHost(environment, oneShot);
        var hostEnvironment = host.Services.GetRequiredService<IHostEnvironment>();
        // A null environment is the operator who set nothing; the host must read it as Production.
        Assert.Equal(environment ?? Environments.Production, hostEnvironment.EnvironmentName);

        using var scope = host.Services.CreateScope();
        var resolved = FixturePorts.Where(port => scope.ServiceProvider.GetService(port) is not null)
            .Select(port => port.Name).ToArray();
        Assert.Equal(
            hostEnvironment.IsProduction() ? Array.Empty<string>() : FixturePorts.Select(port => port.Name).ToArray(),
            resolved);
    }

    private static IHost BuildProgramHost(string? environment, bool oneShot)
    {
        string[] args =
        [
            .. oneShot ? ["migrate"] : Array.Empty<string>(),
            .. environment is null ? Array.Empty<string>() : [$"--environment={environment}"],
            "--ConnectionStrings:Default=Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none",
            "--Database:AllowInsecureConnection=true",
            $"--Jwt:PrivateKeyPem={TestJwtKeys.PrivateKeyPem}",
            $"--Jwt:PublicKeyPem={TestJwtKeys.PublicKeyPem}",
            "--RateLimiting:TrustedProxies:0=10.0.0.0/8",
        ];
        using var capture = new HostCapture();
        using var subscription = DiagnosticListener.AllListeners.Subscribe(capture);
        Capturing.Value = true;
        try
        {
            typeof(Program).Assembly.EntryPoint!.Invoke(null, [args]);
        }
        catch (TargetInvocationException e) when (e.InnerException is HostCapturedException)
        {
        }
        finally
        {
            Capturing.Value = false;
        }
        return capture.Host ?? throw new InvalidOperationException("Program.cs returned without building a host.");
    }

    private sealed class HostCapturedException : Exception;

    // Stops Program.cs at HostBuilt, before the CLI dispatch or Kestrel, the way
    // WebApplicationFactory does. Capturing keeps hosts other tests build concurrently out.
    private sealed class HostCapture
        : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly List<IDisposable> _subscriptions = [];

        public IHost? Host { get; private set; }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.Extensions.Hosting")
                _subscriptions.Add(listener.Subscribe(this));
        }

        public void OnNext(KeyValuePair<string, object?> e)
        {
            if (e.Key != "HostBuilt" || !Capturing.Value || Host is not null) return;
            Host = (IHost)e.Value!;
            throw new HostCapturedException();
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void Dispose() => _subscriptions.ForEach(s => s.Dispose());
    }
}
