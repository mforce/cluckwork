using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// #1284: passkeys written straight to the table, with the user's count, for
// the cases that need many, need them fast, or run while passkeys are off.
internal static partial class TestHarness
{
    public static Task SeedPasskeysAsync(this CluckworkWebApplicationFactory factory, Guid accountId, Guid userId, int count) =>
        factory.WithTenantScopeAsync(accountId, async db =>
        {
            for (var i = 0; i < count; i++)
                db.UserPasskeys.Add(new IdentityUserPasskey<Guid>
                {
                    CredentialId = Guid.NewGuid().ToByteArray(),
                    UserId = userId,
                    Data = new IdentityPasskeyData
                    {
                        PublicKey = [1], Name = $"Seeded {i}", CreatedAt = DateTimeOffset.UtcNow,
                        AttestationObject = [1], ClientDataJson = "{}"u8.ToArray(),
                    },
                });
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.PasskeyCount += count;
            await db.SaveChangesAsync();
        });

    // #1285: a passkey the authenticator can sign with, written straight to
    // the table for a factory with no registration helpers.
    public static Task SeedPasskeyAsync(this CluckworkWebApplicationFactory factory, Guid accountId, Guid userId, SoftwareAuthenticator key)
    {
        key.UserHandle = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(userId.ToString()));
        return factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.UserPasskeys.Add(new IdentityUserPasskey<Guid>
            {
                CredentialId = key.CredentialId,
                UserId = userId,
                Data = new IdentityPasskeyData
                {
                    PublicKey = key.PublicKey, Name = "Seeded", CreatedAt = DateTimeOffset.UtcNow,
                    IsUserVerified = true, IsBackupEligible = true, IsBackedUp = true,
                    AttestationObject = [1], ClientDataJson = "{}"u8.ToArray(),
                },
            });
            (await db.Users.SingleAsync(u => u.Id == userId)).PasskeyCount++;
            await db.SaveChangesAsync();
        });
    }

    // The user's PasskeyCount and the rows that back it.
    public static Task<(int Count, int Rows)> PasskeyStateAsync(this CluckworkWebApplicationFactory factory, Guid accountId, Guid userId) =>
        factory.WithTenantScopeAsync(accountId, async db => (
            await db.Users.Where(u => u.Id == userId).Select(u => u.PasskeyCount).SingleAsync(),
            await db.UserPasskeys.CountAsync(p => p.UserId == userId)));
}
