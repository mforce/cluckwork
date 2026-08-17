namespace Cluckwork.Api.Configuration;

// #543 — configuration for the shared-state (Redis) backend. A blank
// ConnectionString means "no Redis": the app runs on the in-process
// implementations (Option B, single instance). KeyNamespace prefixes every
// Redis key so environments sharing one Redis do not collide.
public sealed class SharedStateOptions
{
    public const string SectionName = "SharedState";
    public RedisOptions Redis { get; init; } = new();

    public sealed class RedisOptions
    {
        public string ConnectionString { get; init; } = "";
        public string KeyNamespace { get; init; } = "cluckwork";
    }
}
