namespace Cluckwork.Api.IntegrationTests;

using System.Security.Claims;
using Cluckwork.Api.Middleware;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Extensions.Hosting;

public sealed class TenantResolutionMiddlewareTests
{
    [Fact]
    public async Task AuthenticatedRequest_MissingAccountId_IsRejectedBeforeDownstream()
    {
        var result = await InvokeTenantAsync(
            new Claim("sub", Guid.NewGuid().ToString()));

        Assert.Equal(
            (Status: StatusCodes.Status401Unauthorized, DownstreamInvocations: 0,
                TenantResolved: false, UserResolved: false),
            result);
    }

    [Fact]
    public async Task AuthenticatedRequest_MalformedAccountId_IsRejectedBeforeDownstream()
    {
        var result = await InvokeTenantAsync(
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("account_id", "not-a-guid"));

        Assert.Equal(
            (Status: StatusCodes.Status401Unauthorized, DownstreamInvocations: 0,
                TenantResolved: false, UserResolved: false),
            result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    public async Task AuthenticatedRequest_InvalidSub_IsRejectedBeforeDownstream(string? sub)
    {
        var claims = new List<Claim>
        {
            new("account_id", Guid.NewGuid().ToString()),
        };
        if (sub is not null)
            claims.Add(new Claim("sub", sub));

        var result = await InvokeTenantAsync(claims.ToArray());

        Assert.Equal(
            (Status: StatusCodes.Status401Unauthorized, DownstreamInvocations: 0,
                TenantResolved: true, UserResolved: false),
            result);
    }

    [Fact]
    public async Task AuthenticatedRequest_ValidClaims_ResolvesScopesAndContinues()
    {
        var accountId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var tenant = new TenantContext();
        var user = new CurrentUserContext();
        var flockScope = new FlockScope();
        var finalInvocations = 0;
        var context = AuthenticatedContext(
            new Claim("account_id", accountId.ToString()),
            new Claim("sub", userId.ToString()),
            new Claim("email", "owner@test.local"),
            new Claim("role", Roles.Owner));

        await InvokePipelineAsync(context, tenant, user, flockScope, () => finalInvocations++);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(1, finalInvocations);
        Assert.True(tenant.IsResolved);
        Assert.Equal(accountId, tenant.AccountId);
        Assert.True(user.IsResolved);
        Assert.Equal(userId, user.UserId);
        Assert.Equal("owner@test.local", user.Email);
        Assert.Equal([Roles.Owner], user.Roles);
        Assert.True(flockScope.IsResolved);
        Assert.True(flockScope.IsUnrestricted);
        Assert.Empty(flockScope.AssignedFlockIds);
    }

    [Fact]
    public async Task AnonymousHealthRequest_RemainsUnresolvedAndDoesNotQueryDatabase()
    {
        var tenant = new TenantContext();
        var user = new CurrentUserContext();
        var flockScope = new FlockScope();
        var finalInvocations = 0;
        var context = new DefaultHttpContext();
        context.Request.Path = "/health/live";

        await InvokePipelineAsync(context, tenant, user, flockScope, () => finalInvocations++);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(1, finalInvocations);
        Assert.False(tenant.IsResolved);
        Assert.False(user.IsResolved);
        Assert.True(flockScope.IsResolved);
        Assert.True(flockScope.IsUnrestricted);
        Assert.Empty(flockScope.AssignedFlockIds);
    }

    private static async Task<(int Status, int DownstreamInvocations,
        bool TenantResolved, bool UserResolved)> InvokeTenantAsync(params Claim[] claims)
    {
        var downstreamInvocations = 0;
        var tenant = new TenantContext();
        var user = new CurrentUserContext();
        var context = AuthenticatedContext(claims);
        var middleware = new TenantResolutionMiddleware(_ =>
        {
            downstreamInvocations++;
            return Task.CompletedTask;
        });
        using var serilog = new LoggerConfiguration().CreateLogger();

        await middleware.InvokeAsync(
            context,
            tenant,
            user,
            new DiagnosticContext(serilog),
            NullLogger<TenantResolutionMiddleware>.Instance);

        return (context.Response.StatusCode, downstreamInvocations,
            tenant.IsResolved, user.IsResolved);
    }

    private static async Task InvokePipelineAsync(
        DefaultHttpContext context,
        TenantContext tenant,
        CurrentUserContext user,
        FlockScope flockScope,
        Action onFinal)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=127.0.0.1;Port=1;Database=unreachable;Username=none;" +
                "Password=none;Timeout=1;Command Timeout=1")
            .Options;
        await using var db = new AppDbContext(options, tenant, flockScope);
        var flockMiddleware = new FlockScopeResolutionMiddleware(_ =>
        {
            onFinal();
            return Task.CompletedTask;
        });
        var tenantMiddleware = new TenantResolutionMiddleware(
            nextContext => flockMiddleware.InvokeAsync(nextContext, flockScope, user, db));
        using var serilog = new LoggerConfiguration().CreateLogger();

        await tenantMiddleware.InvokeAsync(
            context,
            tenant,
            user,
            new DiagnosticContext(serilog),
            NullLogger<TenantResolutionMiddleware>.Instance);
    }

    private static DefaultHttpContext AuthenticatedContext(params Claim[] claims) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test-authentication")),
    };
}
