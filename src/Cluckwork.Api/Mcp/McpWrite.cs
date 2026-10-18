using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cluckwork.Api.Middleware;
using Cluckwork.Domain.Common;
using FluentValidation;
using ModelContextProtocol;

namespace Cluckwork.Api.Mcp;

// #809 — the one claim, execute and publish run every write tool makes through #307's
// IdempotentUnit. The claim is keyed on the tool name, so tools cannot collide, and on the
// user, so two people's agents choosing the same key do not refuse or replay each other.
// Only a successful Result is published; a refusal releases the claim.
internal static class McpWrite
{
    public static async Task<T> RunAsync<T>(
        IdempotentUnit idempotency, McpCallContext call, string tool, string idempotencyKey, object arguments,
        Func<Task<Result<T>>> once, Func<T, Task<T>> replayed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new McpException("idempotencyKey is required.");

        var scope = new IdempotencyScope(
            call.AccountId,
            Sha256($"mcp:tool:{tool}"),
            Sha256($"{call.UserId}:{idempotencyKey}"),
            Sha256(JsonSerializer.Serialize(arguments)));

        var result = await idempotency.RunAsync(scope, async () =>
        {
            var done = await once();
            return done.IsSuccess
                ? new IdempotentResponse(true, StatusCodes.Status200OK, "application/json", JsonSerializer.Serialize(done.Value))
                : new IdempotentResponse(false, StatusCodes.Status422UnprocessableEntity, "text/plain",
                    $"{done.Error.Code}: {done.Error.Description}");
        }, ct);

        // A replay never runs `once`, so the tool re-checks what the caller may still see.
        return result switch
        {
            IdempotentResult.Replayed replay => await replayed(Deserialize<T>(replay.Response)),
            IdempotentResult.Executed { Response.Succeeded: true } executed => Deserialize<T>(executed.Response),
            IdempotentResult.Executed failed => throw new McpException(failed.Response.Body),
            IdempotentResult.KeyReused => throw new McpException(
                "Idempotency.KeyReused: this idempotencyKey was already used with different values. "
                + "Use a new key for a new recording."),
            _ => throw new McpException(
                "Idempotency.InProgress: a call with this idempotencyKey is still running. Retry shortly."),
        };
    }

    // Each tool's idempotencyKey description: this, then where to check before an old retry.
    public const string KeyDescription =
        "A key you choose for this recording. A successful result is kept for at least 48 hours. "
        + "Until then, resending the same call with the same key returns that result instead of recording again, "
        + "and reusing the key with different values is refused. After 48 hours the key may be purged and a retry "
        + "may record again. ";

    // The REST route's validator, its messages joined under one refusal code.
    public static async Task<Result> ValidateAsync<TCommand>(
        IValidator<TCommand> validator, TCommand command, string code, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(command, ct);
        return validation.IsValid
            ? Result.Success()
            : Result.Failure(Error.Validation(code, string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));
    }

    private static T Deserialize<T>(IdempotentResponse response) => JsonSerializer.Deserialize<T>(response.Body)!;

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
