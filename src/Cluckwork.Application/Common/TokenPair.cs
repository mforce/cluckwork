namespace Cluckwork.Application.Common;

public sealed record TokenPair(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiry,
    // #532 — the farm the token pair belongs to. Login/ChangeOwnPassword know it
    // at mint time; Refresh recovers it from the stored token row. The
    // per-farm refresh cookie (#532 per-farm rename) needs it to know WHICH
    // cookie name to write, because the token value itself is opaque.
    Guid AccountId);
