namespace Cluckwork.Api.Middleware;

// The one logout predicate. The credential check, flock-scope resolution and
// the Owner two-factor gate all let logout through, so a superseded or gated
// bearer can still end its session (#364, #1281).
internal static class LogoutPath
{
    private const string Path = "/api/v1/auth/logout";

    // Endpoint routing accepts the conventional trailing-slash form too. Keep
    // both spellings reachable, without widening the exemption to descendants
    // such as /auth/logout/anything.
    public static bool Matches(PathString path) => path == Path || path == $"{Path}/";
}
