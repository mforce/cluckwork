using Cluckwork.Domain.Common;

namespace Cluckwork.Application.Modules.Access.Contracts;

public interface IAccessAccountLifecycle
{
    Task<Result<AccountLifecycleOutcome>> SuspendAccountAsync(Guid accountId, string? reason, CancellationToken ct);

    Task<Result<AccountLifecycleOutcome>> ReactivateAccountAsync(Guid accountId, string? reason, CancellationToken ct);

    Task<Result<AccountRenameOutcome>> RenameAccountAsync(
        string currentSlug, string? newSlug, string? reason, CancellationToken ct);
}
