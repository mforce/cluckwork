namespace Cluckwork.Application.Modules.Access.Contracts;

// #797 — the OAuth half of the housekeeping sweep. The sweep owns the thresholds; this
// deletes dead tokens and authorizations created before pruneBefore, then applications
// nobody approved that are older than unapprovedWindow.
public interface IOAuthPurge
{
    Task<OAuthPurgeResult> PurgeAsync(DateTimeOffset pruneBefore, TimeSpan unapprovedWindow, CancellationToken ct);
}
