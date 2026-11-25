using Cluckwork.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1282 decision 3: one WebAuthn ceremony's server-side state, inserted by the
// options route and deleted once, by DELETE … RETURNING, when the ceremony is
// finished. No AccountId: a sign-in ceremony has no farm until it ends, and a
// bound one stores the user, whose id fixes the farm. A platform table read
// only by key, as refresh_tokens is before sign-in. State holds the challenge,
// so the type is a credential (G7.7); a class, it prints only its type name.
[Credential]
public sealed class PasskeyCeremony
{
    public Guid Id { get; set; }
    public CeremonyPurpose Purpose { get; set; }
    public Guid? UserId { get; set; }

    // Identity's state string: the challenge and, for a bound ceremony, the user.
    public string State { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}

public enum CeremonyPurpose : short
{
    SignIn = 1,
    SecondStep = 2,
    StepUp = 3,
    Registration = 4,
}

public sealed class PasskeyCeremonyConfiguration : IEntityTypeConfiguration<PasskeyCeremony>
{
    public void Configure(EntityTypeBuilder<PasskeyCeremony> builder)
    {
        builder.ToTable("passkey_ceremonies");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Purpose).HasConversion<short>();
        builder.Property(c => c.State).IsRequired();
    }
}
