namespace Cluckwork.Application.Common;

public static class AppError
{
    public static Error Unauthorized() =>
        new("Auth.Unauthorized", "You are not authorized to perform this action.");

    public static Error Forbidden() =>
        new("Auth.Forbidden", "You do not have permission to access this resource.");

    public static Error TenantMismatch() =>
        new("Tenant.Mismatch", "Resource does not belong to this account.");

    public static Error IdempotencyReplay() =>
        new("Idempotency.Replay", "Request replayed from idempotency key.");
}
