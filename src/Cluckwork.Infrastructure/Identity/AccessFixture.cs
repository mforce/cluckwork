using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Application.Features.Users;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Identity;

public sealed class AccessFixture(AppDbContext db, IFlockLookup flocks, IAuditWriter audit) : IAccessFixture
{
    public async Task EnsureFlockAssignmentAsync(
        Guid accountId, Guid userId, string email, Guid flockId, CancellationToken ct = default)
    {
        var existingAssignments = await db.UserRoleAssignments.AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);
        if (existingAssignments.Any(a => a.FlockId == flockId)) return;

        // No foreign key ties an assignment's user to its farm, so check here.
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.AccountId == accountId, ct))
            throw new InvalidOperationException($"User {userId} is not in farm {accountId}.");

        // Tenant- and flock-scope-filtered, so another farm's flock reads as missing.
        var flock = await flocks.GetAsync(flockId, ct)
            ?? throw new InvalidOperationException($"Simulation flock {flockId} does not exist.");

        var assignment = UserRoleAssignment.Create(
            Guid.NewGuid(), accountId, userId, farmId: null, houseId: null, flockId);
        await db.UserRoleAssignments.AddAsync(assignment, ct);

        await audit.WriteAsync(
            AuditActions.UserFlockAssign,
            "User",
            userId,
            details: new { Email = email, Flock = flock.Name },
            ct: ct);

        await db.SaveChangesAsync(ct);
    }
}
