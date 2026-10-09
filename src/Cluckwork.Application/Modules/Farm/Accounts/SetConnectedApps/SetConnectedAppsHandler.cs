using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Farm.Contracts;
using Cluckwork.Domain.Modules.Farm.Accounts;

namespace Cluckwork.Application.Modules.Farm.Accounts.SetConnectedApps;

// #1146 — the farm's connected-apps switch, under the same Version token as a Farm
// settings save, so a racing save or a second Owner gets 409 instead of a silent overwrite.
public sealed class SetConnectedAppsHandler(IAccountRepository accounts, IUnitOfWork unitOfWork, IAuditWriter audit)
{
    public async Task<Result> HandleAsync(SetConnectedAppsCommand command, CancellationToken ct)
    {
        var account = await accounts.GetCurrentTrackedAsync(ct);
        if (account is null)
            return Result.Failure(Error.NotFound(nameof(Account), "current"));

        if (command.Version != account.Version)
            return Result.Failure(Error.Conflict(
                "Account.VersionMismatch",
                "The farm was changed by someone else. Reload and try again."));

        var before = account.AllowConnectedApps;
        if (!account.SetConnectedApps(command.Allow!.Value))
            return Result.Success();

        await audit.WriteAsync(
            AuditActions.AccountUpdateSettings, nameof(Account), account.Id,
            reason: null,
            details: new
            {
                before = new { AllowConnectedApps = before },
                after = new { account.AllowConnectedApps },
            },
            ct: ct);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
