namespace Cluckwork.Application.Modules.Farm.Contracts;

// #1146 — Allow is nullable only so an omitted field is a 400, never a silent false.
public sealed record SetConnectedAppsCommand(bool? Allow, int Version);
