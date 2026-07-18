namespace Cluckwork.Infrastructure.Identity;

using System.Security.Cryptography;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed class IdentityProvider(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    IJwtTokenService jwtTokenService,
    AppDbContext db,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider) : IIdentityProvider
{
    // Pre-computed V3 PBKDF2 hash used to equalize login timing when the email is not found,
    // preventing user enumeration via response-time analysis.
    private static readonly string DummyHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!9x");

    public async Task<Result<TokenPair>> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            // Always pay the PBKDF2 cost so that "user not found" and "wrong password"
            // are indistinguishable by timing.
            userManager.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), DummyHash, password);
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidCredentials", "Invalid email or password."));
        }

        if (!await userManager.CheckPasswordAsync(user, password))
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidCredentials", "Invalid email or password."));

        var (rawToken, tokenHash) = GenerateRefreshToken();
        db.RefreshTokens.Add(NewToken(user, tokenHash));
        await db.SaveChangesAsync(ct);

        var roles = await userManager.GetRolesAsync(user);
        return Result.Success(jwtTokenService.CreateTokenPair(user, [.. roles], rawToken));
    }

    public async Task<Result<TokenPair>> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var presentedHash = Hash(refreshToken);
        var now = timeProvider.GetUtcNow();

        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == presentedHash, ct);
        if (stored is null)
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidRefreshToken", "Refresh token is invalid."));

        // Presenting an already-rotated/revoked token means it was replayed — treat as a
        // possible theft and revoke every active token for the user (breaks the chain).
        if (stored.RevokedAt is not null)
        {
            await RevokeAllActiveForUserAsync(stored.UserId, now, ct);
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidRefreshToken", "Refresh token is invalid."));
        }

        if (stored.ExpiresAt <= now)
            return Result.Failure<TokenPair>(Error.Validation("Identity.ExpiredRefreshToken", "Refresh token has expired."));

        var user = await userManager.FindByIdAsync(stored.UserId.ToString());
        if (user is null)
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidRefreshToken", "Refresh token is invalid."));

        // Rotate: revoke the presented token and issue a fresh one.
        var (rawToken, newHash) = GenerateRefreshToken();
        stored.RevokedAt = now;
        stored.ReplacedByTokenHash = newHash;
        db.RefreshTokens.Add(NewToken(user, newHash));
        await db.SaveChangesAsync(ct);

        // Roles re-read on every refresh so a demotion takes effect within one
        // access-token lifetime, not at next login.
        var roles = await userManager.GetRolesAsync(user);
        return Result.Success(jwtTokenService.CreateTokenPair(user, [.. roles], rawToken));
    }

    public async Task<Result<Guid>> CreateUserAsync(
        Guid accountId, string email, string password, bool isAdmin, CancellationToken ct = default)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            AccountId = accountId
        };

        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
            return Result.Failure<Guid>(Error.Validation("Users.CreateFailed", Describe(created)));

        if (isAdmin)
        {
            if (!await roleManager.RoleExistsAsync(DatabaseSeeder.AdminRole))
                await roleManager.CreateAsync(new ApplicationRole { Id = Guid.NewGuid(), Name = DatabaseSeeder.AdminRole });

            var addedToRole = await userManager.AddToRoleAsync(user, DatabaseSeeder.AdminRole);
            if (!addedToRole.Succeeded)
            {
                // Don't leave a half-created admin behind as a silent worker.
                await userManager.DeleteAsync(user);
                return Result.Failure<Guid>(Error.Validation("Users.CreateFailed", Describe(addedToRole)));
            }
        }

        return Result.Success(user.Id);
    }

    public async Task<IReadOnlyList<UserSummary>> ListUsersAsync(Guid accountId, CancellationToken ct = default)
    {
        var adminIds = await (
            from userRole in db.UserRoles
            join role in db.Roles on userRole.RoleId equals role.Id
            where role.Name == DatabaseSeeder.AdminRole
            select userRole.UserId).ToListAsync(ct);

        return await db.Users
            .Where(u => u.AccountId == accountId)
            .OrderBy(u => u.Email)
            .Select(u => new UserSummary(
                u.Id, u.Email!, u.DisplayName,
                adminIds.Contains(u.Id) ? DatabaseSeeder.AdminRole : "Worker"))
            .ToListAsync(ct);
    }

    private static string Describe(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(e => e.Description));

    public async Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var presentedHash = Hash(refreshToken);
        var stored = await db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == presentedHash && t.RevokedAt == null, ct);

        if (stored is null) return;

        stored.RevokedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private RefreshToken NewToken(ApplicationUser user, string tokenHash)
    {
        var now = timeProvider.GetUtcNow();
        return new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            AccountId = user.AccountId,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(jwtOptions.Value.RefreshTokenDays)
        };
    }

    private async Task RevokeAllActiveForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in active)
            token.RevokedAt = now;

        if (active.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    // 256-bit random token; only the SHA-256 hash is persisted.
    private static (string Raw, string Hash) GenerateRefreshToken()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (raw, Hash(raw));
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
