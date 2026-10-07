using System.Xml.Linq;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Api.Hosting;

// #794 — the certificate encrypts keys only as they are written; the framework still reads
// a plaintext key, and a database-backup reader could forge payloads with it. Registered
// only for a Production serving process; one-shot verbs never start the host.
internal sealed class PlaintextDataProtectionKeyGuard(IServiceScopeFactory scopes) : IHostedLifecycleService
{
    // StartingAsync runs after ValidateOnStart and before any hosted service starts, so the
    // check precedes Kestrel and the framework's own key-ring load.
    public Task StartingAsync(CancellationToken cancellationToken) =>
        EnsureNoPlaintextDataProtectionKeysAsync(scopes, cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task EnsureNoPlaintextDataProtectionKeysAsync(
        IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Pending migrations leave no table yet; /health/ready reports that state (#263).
        var tableExists = await db.Database
            .SqlQuery<bool>($"SELECT to_regclass('\"DataProtectionKeys\"') IS NOT NULL AS \"Value\"")
            .SingleAsync(cancellationToken);
        if (!tableExists)
            return;

        var plaintext = PlaintextKeyIds(
            await db.DataProtectionKeys.AsNoTracking().Select(k => k.Xml).ToListAsync(cancellationToken));
        if (plaintext.Count == 0)
            return;

        throw new InvalidOperationException(
            $"Production found {plaintext.Count} Data Protection key(s) stored without encryption "
            + $"(key id: {string.Join(", ", plaintext)}). Delete those rows from \"DataProtectionKeys\" "
            + "while no serving instance runs, then start again; tokens they protected stop validating. "
            + "See docs/runbooks/data-protection-key-ring.md.");
    }

    // The framework marks key material that still needs encrypting with
    // requiresEncryption="true" and replaces that element once it is encrypted.
    internal static IReadOnlyList<string> PlaintextKeyIds(IEnumerable<string?> keyXml)
    {
        XName requiresEncryption = XName.Get("requiresEncryption", "http://schemas.asp.net/2015/03/dataProtection");
        return
        [
            .. keyXml.OfType<string>()
                .Select(XElement.Parse)
                .Where(key => key.Descendants().Any(e => (string?)e.Attribute(requiresEncryption) == "true"))
                .Select(key => (string?)key.Attribute("id") ?? "(no id)"),
        ];
    }
}
