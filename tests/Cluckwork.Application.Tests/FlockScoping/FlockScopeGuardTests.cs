using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Application.Tests.FlockScoping;

public sealed class FlockScopeGuardTests
{
    [Fact]
    public async Task CheckAsync_WithUnresolvedActor_ReturnsUnauthorizedWithoutDatabaseAccess()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=1;Database=unreachable;Username=unreachable;Password=unreachable")
            .Options;
        var tenant = new TenantContext();
        using var db = new AppDbContext(options, tenant, new FlockScope());
        var guard = new FlockScopeGuard(new AccessLookup(db, tenant), new CurrentUserContext());

        var result = await guard.CheckAsync(Guid.NewGuid());

        Assert.Equal(AppError.Unauthorized(), result.Error);
    }
}
