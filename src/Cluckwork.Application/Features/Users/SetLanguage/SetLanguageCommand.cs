namespace Cluckwork.Application.Features.Users.SetLanguage;

// The already-canonicalised (trimmed + lowercased) preference, or null to clear.
public sealed record SetLanguageCommand(string? Language);
