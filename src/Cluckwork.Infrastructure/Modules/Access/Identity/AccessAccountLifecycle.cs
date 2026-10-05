using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// Lifecycle verbs must remain independent of the credential and shared-state graph.
public sealed class AccessAccountLifecycle(
    AccountSuspensionService suspension,
    AccountRenameService rename) : IAccessAccountLifecycle
{
    public Task<Result<AccountLifecycleOutcome>> SuspendAccountAsync(
        Guid accountId, string? reason, CancellationToken ct) =>
        suspension.SuspendAsync(accountId, reason, ct);

    public Task<Result<AccountLifecycleOutcome>> ReactivateAccountAsync(
        Guid accountId, string? reason, CancellationToken ct) =>
        suspension.ReactivateAsync(accountId, reason, ct);

    public Task<Result<AccountRenameOutcome>> RenameAccountAsync(
        string currentSlug, string? newSlug, string? reason, CancellationToken ct) =>
        rename.RenameAsync(currentSlug, newSlug, reason, ct);
}
