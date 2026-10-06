namespace Cluckwork.Application.Modules.Access.Contracts;

// The already-canonicalised (trimmed + lowercased) preference, or null to clear.
public sealed record SetLanguageCommand(string? Language);
