using System.Collections.Concurrent;
using Cluckwork.Api.Modules.Access.OAuth;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// #1278: the host for the two-factor login guards. Its own collection, so the
// log tap and the clock see only these tests.
public sealed class SecondFactorFactory : CluckworkWebApplicationFactory
{
    public const string OAuthProbePath = "/test/two-factor/oauth";

    public LogSink Sink { get; } = new();

    public TransientCommitFaultInterceptor CommitFault { get; } = new();

    internal SettableClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILogEventSink>(Sink);
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IStartupFilter, OAuthProbe>();
            services.AddDbContext<AppDbContext>((_, options) => options.AddInterceptors(CommitFault));
        });
    }

    public IReadOnlyList<LogEvent> EventsFor(string securityEvent) =>
        [.. Sink.Events.Where(e => e.Properties.TryGetValue("SecurityEvent", out var value)
            && value is ScalarValue { Value: string name } && name == securityEvent)];

    public sealed class LogSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    // A connected-app route that answers with the token's two_factor claim.
    private sealed class OAuthProbe : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            var endpoints = (Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app.Properties["__EndpointRouteBuilder"]!;
            endpoints.MapGet(OAuthProbePath, (Microsoft.AspNetCore.Http.HttpContext context) => context.User.FindFirst("two_factor")?.Value ?? "absent")
                .AcceptOAuthTokens(OAuthScopes.ReadFarm)
                .RequireAuthorization();
        };
    }
}

// The wall clock plus an offset, or frozen at one instant. Tokens minted under
// an offset are checked against the wall clock by the bearer handler, so a test
// that moves the clock signs in only through the auth endpoints.
internal sealed class SettableClock : TimeProvider
{
    private readonly Lock _gate = new();
    private TimeSpan _offset;
    private DateTimeOffset? _frozenAt;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _frozenAt ?? DateTimeOffset.UtcNow + _offset;
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            if (_frozenAt is { } at) _frozenAt = at + by;
            else _offset += by;
        }
    }

    public void Freeze()
    {
        lock (_gate) _frozenAt ??= DateTimeOffset.UtcNow + _offset;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _offset = TimeSpan.Zero;
            _frozenAt = null;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SecondFactorCollection : ICollectionFixture<SecondFactorFactory>
{
    public const string Name = "second-factor";
}
