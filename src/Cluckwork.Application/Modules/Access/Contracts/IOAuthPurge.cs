namespace Cluckwork.Application.Modules.Access.Contracts;

// #797 — the OAuth half of the housekeeping sweep. The sweep owns the cutoffs; this
// deletes dead tokens and authorizations created before pruneBefore, then applications
// registered before unapprovedBefore that nobody approved.
public interface IOAuthPurge
{
    Task<OAuthPurgeResult> PurgeAsync(DateTimeOffset pruneBefore, DateTimeOffset unapprovedBefore, CancellationToken ct);
}
