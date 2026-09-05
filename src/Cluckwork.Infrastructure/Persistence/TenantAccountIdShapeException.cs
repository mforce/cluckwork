namespace Cluckwork.Infrastructure.Persistence;

// #673 — both write-side tenant layers key on AccountId being a non-nullable
// Guid, and both used to fall THROUGH for any other shape: the model walk in
// AppDbContext.OnModelCreating gave a Guid? or a converted AccountId no
// concurrency token, and TenantStampInterceptor skipped any value that did not
// box to a Guid. A tenant-owned entity mapped with `Guid? AccountId` therefore
// reopened the #562 hole — a detached Update/Remove naming another farm's row
// wrote through, with every test green.
//
// Both layers now throw this instead of continuing: at model build for the
// wrong CLR type (so a mis-typed AccountId fails every boot and every test),
// and at SaveChanges for a value that is not a Guid. Distinct from
// TenantWriteMismatchException, which means a real cross-tenant VALUE; this one
// means the mapping itself cannot be checked. Caught nowhere, deliberately.
public sealed class TenantAccountIdShapeException : InvalidOperationException
{
    public string EntityType { get; }

    private TenantAccountIdShapeException(string message, string entityType)
        : base(message) => EntityType = entityType;

    public static TenantAccountIdShapeException ForModel(string entityType, Type clrType) =>
        new($"{entityType}.AccountId is mapped as {clrType.Name}, but tenant isolation requires a " +
            "non-nullable Guid: the concurrency-token walk and the write guard can only check that " +
            "shape, so any other one is a silent cross-tenant write (#673).",
            entityType);

    public static TenantAccountIdShapeException ForMapping(string entityType, string state, Type clrType) =>
        new($"Refusing to save {state} {entityType}: its AccountId is mapped as {clrType.Name} rather than " +
            "a non-nullable Guid, so the value this guard sees can be right while the row carries no " +
            "concurrency token to compare it against (#673).",
            entityType);

    public static TenantAccountIdShapeException ForWrite(string entityType, string state, object? value) =>
        new($"Refusing to save {state} {entityType}: its AccountId is " +
            $"{(value is null ? "null" : $"a {value.GetType().Name}")} rather than a Guid, so the write " +
            "guard cannot check it against the resolved tenant (#673).",
            entityType);
}
