namespace Cluckwork.Domain.Auditing;

// #800 — the connected app (OAuth client) a person acted through. The person stays the
// actor (#500); this is provenance beside them. Name is the client's self-asserted DCR
// name, already stripped of controls and bidi characters (#797), and may be absent.
public sealed record ConnectedApp(string ClientId, string? Name);
