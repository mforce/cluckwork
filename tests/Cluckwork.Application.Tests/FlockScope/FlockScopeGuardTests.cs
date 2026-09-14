namespace Cluckwork.Application.Tests.FlockScope;

using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

public sealed class FlockScopeGuardTests
{
    [Fact]
    public async Task CheckAsync_WithUnresolvedActor_ReturnsUnauthorizedWithoutDatabaseAccess()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=1;Database=unreachable;Username=unreachable;Password=unreachable")
            .Options;
        using var db = new AppDbContext(options, new TenantContext(), new FlockScope());
        var guard = new FlockScopeGuard(db, new CurrentUserContext());

        var result = await guard.CheckAsync(Guid.NewGuid());

        Assert.Equal(AppError.Unauthorized(), result.Error);
    }
}
