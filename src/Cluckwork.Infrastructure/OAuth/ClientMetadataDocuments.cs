using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Cluckwork.Infrastructure.OAuth;

// OAuth:ClientMetadata. Both keys are optional. A deployment without outbound https turns
// Enabled off, so discovery stops offering what it cannot fetch. PrivateHosts names hosts
// that may resolve to a private network, such as a document served inside a test stack.
public sealed record ClientMetadataOptions(bool Enabled = true, IReadOnlySet<string>? PrivateHosts = null);

// #1148 — a client_id that is an https URL names the client's metadata document. Before
// OpenIddict looks the client up, this keeps an ordinary application row for it, built
// from the document by the rules a registered client meets (#797). Everything after
// (redirect matching, consent, tokens, Connected apps, audit, the purge) then treats it
// like any other client. The row is the cache: it lives in Postgres, so every replica
// reads the same copy (#271), and its expiry rides in OpenIddict's Properties column.
// Only the authorization request fetches; the token endpoint uses the row the code was
// issued against.
internal sealed class ClientMetadataDocuments(
    ClientMetadataOptions options,
    IServiceScopeFactory scopes,
    ClientMetadataFetcher fetcher,
    IFixedWindowCounter counter,
    TimeProvider clock,
    ILogger<ClientMetadataDocuments> logger) : IOpenIddictServerHandler<ValidateAuthorizationRequestContext>
{
    public const string ExpiresAtProperty = "cimd_expires_at";

    // A fetch is spent only when the stored copy is missing or expired. Per URL first, so
    // one busy URL cannot drain the budget every other client shares.
    public static readonly (int Limit, TimeSpan Window) PerUrlBudget = (10, TimeSpan.FromMinutes(5));
    public static readonly (int Limit, TimeSpan Window) GlobalBudget = (60, TimeSpan.FromMinutes(1));

    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateAuthorizationRequestContext>()
            .UseScopedHandler<ClientMetadataDocuments>()
            .SetOrder(OpenIddictServerHandlers.Authentication.ValidateClientIdParameter.Descriptor.Order + 500)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public async ValueTask HandleAsync(ValidateAuthorizationRequestContext context)
    {
        if (!ClientMetadata.IsDocumentUrl(context.ClientId))
            return;

        // Turned off, a copy stored earlier must not keep working either.
        if (!options.Enabled)
        {
            context.Reject(Errors.InvalidRequest, "This server does not accept client ID metadata documents.");
            return;
        }

        var ready = await EnsureFreshAsync(context.ClientId, context.CancellationToken);
        if (ready.IsFailure)
            context.Reject(ready.Error.Code, ready.Error.Description);
    }

    internal async Task<Result> EnsureFreshAsync(string clientId, CancellationToken ct)
    {
        var url = ClientMetadata.ParseDocumentUrl(clientId);
        if (url.IsFailure)
            return Result.Failure(url.Error);

        // Its own scope: a failed insert must not stay tracked in the request's context,
        // where the consent transaction would save it again, and the request's application
        // cache must first see the row after this refresh.
        await using var scope = scopes.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var row = await applications.FindByClientIdAsync(clientId, ct);
        if (row is not null && ExpiresAt(await applications.GetPropertiesAsync(row, ct)) > clock.GetUtcNow())
            return Result.Success();

        if (!await SpendBudgetAsync(clientId, ct))
            return Refuse(Errors.TemporarilyUnavailable, "Too many metadata documents were fetched recently. Try again later.");

        var fetched = await fetcher.FetchAsync(url.Value, ct);
        if (fetched.IsFailure)
        {
            logger.LogInformation("Client metadata document from {Host} refused: {Reason}.", url.Value.Host, fetched.Error.Code);
            return Refuse(Errors.InvalidRequest, fetched.Error.Description);
        }

        var descriptor = ClientMetadata.FromDocument(clientId, fetched.Value.Json);
        if (descriptor.IsFailure)
            return Refuse(Errors.InvalidRequest, descriptor.Error.Description);
        descriptor.Value.Properties[ExpiresAtProperty] =
            JsonSerializer.SerializeToElement((clock.GetUtcNow() + fetched.Value.Lifetime).ToUnixTimeSeconds());

        try
        {
            if (row is null)
                await applications.CreateAsync(descriptor.Value, ct);
            else
                await applications.UpdateAsync(row, descriptor.Value, ct);
        }
        catch (OpenIddictExceptions.ValidationException exception)
        {
            // OpenIddict's own checks, which DCR meets too: an iss parameter in a redirect
            // URI, for one (issuer fixation).
            return Refuse(Errors.InvalidRequest, string.Join(" ", exception.Results.Select(result => result.ErrorMessage)));
        }
        catch (Exception exception) when (exception is OpenIddictExceptions.ConcurrencyException
            || exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } })
        {
            // Another request stored a copy it validated the same way. Either copy is fine.
        }

        return Result.Success();
    }

    private async Task<bool> SpendBudgetAsync(string clientId, CancellationToken ct)
    {
        // A hash, so no client-chosen text reaches the shared store's keys.
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(clientId)));
        return (await counter.IncrementAsync($"oauth-metadata:url:{key}", PerUrlBudget.Window, ct)).Count <= PerUrlBudget.Limit
            && (await counter.IncrementAsync("oauth-metadata:global", GlobalBudget.Window, ct)).Count <= GlobalBudget.Limit;
    }

    private static DateTimeOffset ExpiresAt(IReadOnlyDictionary<string, JsonElement> properties) =>
        properties.TryGetValue(ExpiresAtProperty, out var value) && value.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.MinValue;

    private static Result Refuse(string code, string description) => Result.Failure(new Error(code, description));
}
