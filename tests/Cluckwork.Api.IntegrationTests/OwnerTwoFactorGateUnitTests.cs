using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Cluckwork.Api.Configuration;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Middleware;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cluckwork.Api.IntegrationTests;

// #1281 G6.3: the gate's only inputs are the role claim, the epoch check's
// TwoFactorEnabled and SecondFactorRequiredSince, OwnerGraceDays,
// RequireForOwners and the clock.
// No host: RequestServices resolves only what WriteAsJsonAsync needs and throws
// on anything else, so the gate cannot reach configuration or a per-user flag.
public sealed class OwnerTwoFactorGateUnitTests
{
    private const int Days = 14;
    private const string GatedPath = "/api/v1/flocks";

    private readonly SettableClock clock = new();
    private readonly RecordingGraceStart graceStart = new();
    private readonly RecordingLogger logger = new();
    private readonly List<Type> resolved = [];
    private bool graceStartRegistered = true;
    private bool requireForOwners = true;

    public OwnerTwoFactorGateUnitTests() => clock.Freeze();

    private DateTimeOffset Now => clock.GetUtcNow();

    private DateTimeOffset Deadline(TimeSpan by) => Now - TimeSpan.FromDays(Days) + by;

    [Fact]
    public async Task AnUnenrolledOwner_PassesBeforeTheDeadline()
    {
        var (passed, _) = await RunAsync(Owner(), Check(enrolled: false, Deadline(TimeSpan.FromSeconds(1))));
        Assert.True(passed);
    }

    [Fact]
    public async Task AnUnenrolledOwner_IsRefusedFromTheDeadline_WithNoChallenge()
    {
        var (passed, context) = await RunAsync(Owner(), Check(enrolled: false, Deadline(TimeSpan.Zero)));

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal(EnrolmentErrorCodes.EnrolmentRequired, (await ProblemAsync(context)).Title);
        Assert.False(context.Response.Headers.ContainsKey("WWW-Authenticate"));
    }

    [Fact]
    public async Task AnEnrolledOwner_PassesPastTheDeadline()
    {
        var (passed, _) = await RunAsync(Owner(), Check(enrolled: true, Deadline(-TimeSpan.FromDays(30))));
        Assert.True(passed);
    }

    // A future-dated start cannot exempt anyone: more than 5 minutes ahead
    // counts as past the deadline.
    [Theory]
    [InlineData(299, true)]
    [InlineData(301, false)]
    public async Task AStartAheadOfNow_IsInGraceOnlyWithinFiveMinutes(int secondsAhead, bool expectPassed)
    {
        var (passed, _) = await RunAsync(Owner(), Check(enrolled: false, Now + TimeSpan.FromSeconds(secondsAhead)));
        Assert.Equal(expectPassed, passed);
    }

    [Fact]
    public async Task AnOwnerRequestWithNoCheck_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(Owner(), check: null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Roles.Manager)]
    [InlineData(Roles.ReadOnly)]
    public async Task ANonOwner_IsNeverGated_AndStartsNoGrace(string? role)
    {
        var (passed, _) = await RunAsync(Principal(role), Check(enrolled: false, start: null));

        Assert.True(passed);
        Assert.Empty(graceStart.Calls);
    }

    [Theory]
    [InlineData("POST", "/api/v1/auth/change-password")]
    [InlineData("POST", "/api/v1/auth/step-up")]
    [InlineData("GET", "/api/v1/auth/two-factor")]
    [InlineData("POST", "/api/v1/auth/two-factor/start")]
    [InlineData("POST", "/api/v1/auth/two-factor/confirm/")]
    [InlineData("POST", "/api/v1/auth/logout")]
    public async Task PastTheDeadline_TheEnrolmentPathsStayOpen(string method, string path)
    {
        var (passed, _) = await RunAsync(Owner(), Check(enrolled: false, Deadline(-TimeSpan.FromDays(1))), method, path);
        Assert.True(passed);
    }

    [Theory]
    [InlineData("DELETE", "/api/v1/auth/two-factor")]
    [InlineData("POST", "/api/v1/auth/two-factor/recovery-codes")]
    [InlineData("GET", "/api/v1/auth/change-password")]
    [InlineData("POST", "/api/v1/auth/logout/anything")]
    public async Task PastTheDeadline_ANeighbourOfAnOpenPath_IsRefused(string method, string path)
    {
        var (passed, _) = await RunAsync(Owner(), Check(enrolled: false, Deadline(-TimeSpan.FromDays(1))), method, path);
        Assert.False(passed);
    }

    [Fact]
    public async Task TheExceptionHandlersReExecution_IsNotGated()
    {
        var context = Request(Owner(), check: null);
        context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature { Error = new Exception() });
        Assert.True(await InvokeAsync(context));
    }

    // Enrolled or not, an Owner's request that finds no start sets it to the
    // gate's own now, and that request is in grace.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANullStart_IsSetToNow_AndTheRequestPasses(bool enrolled)
    {
        var (passed, _) = await RunAsync(Owner(), Check(enrolled, start: null));

        Assert.True(passed);
        Assert.Equal(Now, Assert.Single(graceStart.Calls));
    }

    // The write happens once per Owner, so a request that needs no start
    // builds no grace-start port.
    [Fact]
    public async Task ASetStart_IsNotWrittenAgain_AndBuildsNoGraceStart()
    {
        await RunAsync(Owner(), Check(enrolled: false, Deadline(TimeSpan.FromDays(1))));
        Assert.Empty(graceStart.Calls);
        Assert.DoesNotContain(typeof(IOwnerGraceStart), resolved);
    }

    [Fact]
    public async Task AFailedStart_LetsTheRequestThrough_AndLogsOneEvent()
    {
        graceStart.Throw = new InvalidOperationException("the database is down");

        var (passed, _) = await RunAsync(Owner(), Check(enrolled: false, start: null));

        Assert.True(passed);
        var (level, message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains(SecurityEvents.OwnerGraceStartFailed, message, StringComparison.Ordinal);
    }

    // Only the start's own write fails open. A missing registration is a
    // wiring bug and must not pass as a failed write.
    [Fact]
    public async Task AMissingGraceStartRegistration_Throws()
    {
        graceStartRegistered = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(Owner(), Check(enrolled: false, start: null)));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task ACancelledStart_IsRethrown()
    {
        graceStart.Throw = new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() => RunAsync(Owner(), Check(enrolled: false, start: null)));
    }

    // Switched off, the gate refuses nothing and records no start, so turning
    // it back on gives an Owner with no start a fresh grace.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithTheRequirementOff_AnOwnerPassesPastTheDeadline_AndStartsNoGrace(bool started)
    {
        requireForOwners = false;

        var (passed, _) = await RunAsync(Owner(), Check(enrolled: false, started ? Deadline(-TimeSpan.FromDays(30)) : null));

        Assert.True(passed);
        Assert.Empty(graceStart.Calls);
        Assert.DoesNotContain(typeof(IOwnerGraceStart), resolved);
    }

    // The gate cannot read another setting or a per-user exemption: its
    // constructor and InvokeAsync take no configuration, no environment and no
    // options but the one type, which holds the days and the switch and
    // nothing else. What it resolves per request, the strict provider below
    // limits.
    [Fact]
    public void TheGatesInputs_AreTheDaysTheSwitchAndTheClock()
    {
        var gate = typeof(OwnerTwoFactorGateMiddleware);
        var parameters = gate.GetConstructors().SelectMany(c => c.GetParameters())
            .Concat(gate.GetMethod(nameof(OwnerTwoFactorGateMiddleware.InvokeAsync))!.GetParameters())
            .Select(p => p.ParameterType).ToList();

        Assert.Equal(
            [typeof(RequestDelegate), typeof(IOptions<OwnerGraceOptions>), typeof(TimeProvider), typeof(HttpContext)],
            parameters);
        Assert.DoesNotContain(parameters, t => typeof(IConfiguration).IsAssignableFrom(t) || typeof(IHostEnvironment).IsAssignableFrom(t));
        Assert.Equal([nameof(OwnerGraceOptions.OwnerGraceDays), nameof(OwnerGraceOptions.RequireForOwners)],
            typeof(OwnerGraceOptions).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Select(p => p.Name));
    }

    private async Task<(bool Passed, HttpContext Context)> RunAsync(
        ClaimsPrincipal user, CredentialCheck? check, string method = "GET", string path = GatedPath)
    {
        var context = Request(user, check, method, path);
        return (await InvokeAsync(context), context);
    }

    private async Task<bool> InvokeAsync(HttpContext context)
    {
        var passed = false;
        var gate = new OwnerTwoFactorGateMiddleware(
            _ => { passed = true; return Task.CompletedTask; },
            Options.Create(new OwnerGraceOptions { OwnerGraceDays = Days, RequireForOwners = requireForOwners }), clock);
        await gate.InvokeAsync(context);
        return passed;
    }

    private DefaultHttpContext Request(ClaimsPrincipal user, CredentialCheck? check, string method = "GET", string path = GatedPath)
    {
        var context = new DefaultHttpContext { User = user };
        context.RequestServices = new StrictServices(this, context);
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (check is { } value) context.Features.Set<CredentialCheck?>(value);
        return context;
    }

    private static CredentialCheck Check(bool enrolled, DateTimeOffset? start) =>
        new(CredentialVerdict.Current, enrolled, start);

    private static ClaimsPrincipal Owner() => Principal(Roles.Owner);

    private static ClaimsPrincipal Principal(string? role)
    {
        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new("account_id", Guid.NewGuid().ToString()),
        ];
        if (role is not null) claims.Add(new Claim("role", role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer", nameType: null, roleType: "role"));
    }

    private static async Task<ProblemDetails> ProblemAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return (await JsonSerializer.DeserializeAsync<ProblemDetails>(context.Response.Body, JsonSerializerOptions.Web))!;
    }

    // WriteAsJsonAsync's options, the grace start and the event logger; any
    // other lookup throws.
    private sealed class StrictServices(OwnerTwoFactorGateUnitTests test, HttpContext context) : IServiceProvider
    {
        public object GetService(Type serviceType)
        {
            test.resolved.Add(serviceType);
            if (serviceType == typeof(IOptions<HttpJsonOptions>)) return Options.Create(new HttpJsonOptions());
            if (serviceType == typeof(IOwnerGraceStart)) return test.graceStartRegistered ? test.graceStart : null!;
            if (serviceType == typeof(AuthSecurityEventLogger))
                return new AuthSecurityEventLogger(new HttpContextAccessor { HttpContext = context }, test.logger);
            throw new InvalidOperationException($"the gate asked for {serviceType}");
        }
    }

    private sealed class RecordingGraceStart : IOwnerGraceStart
    {
        public List<DateTimeOffset> Calls { get; } = [];

        public Exception? Throw { get; set; }

        public Task StartAsync(Guid accountId, Guid userId, DateTimeOffset now, CancellationToken ct)
        {
            if (Throw is not null) throw Throw;
            Calls.Add(now);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger : ILogger<AuthSecurityEventLogger>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
