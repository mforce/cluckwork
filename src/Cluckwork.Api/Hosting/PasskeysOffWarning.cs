using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.Extensions.Options;

namespace Cluckwork.Api.Hosting;

// #1282 decision 5: a passkey still counts as a factor while passkeys are off,
// so a serving process says once, at start, when passkeys are off and some are
// stored, and names the way out. Registered only for a serving process; a
// one-shot verb never starts the host.
internal sealed class PasskeysOffWarning(
    IServiceScopeFactory scopes, IOptions<PasskeyOptions> options, ILogger<PasskeysOffWarning> logger) : IHostedService
{
    public const string EventName = "Passkeys.OffWithPasskeys";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Origins.Length > 0) return;

        int stored;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            stored = await scope.ServiceProvider.GetRequiredService<IPasskeyCredentials>().CountAllAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A pending migration leaves no table yet; /health/ready reports that state (#263).
            logger.LogDebug(ex, "The passkey count could not be read at start.");
            return;
        }

        if (stored > 0)
            logger.LogWarning(
                "{Event}: Passkeys:Origins is empty, so passkeys are off, and {Count} passkey(s) are stored. "
                + "A passkey still counts as its user's factor. To free those users, run clear-passkeys "
                + "(--all or --farm <farm code>, with --reason), or set Passkeys:Origins again.",
                EventName, stored);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
