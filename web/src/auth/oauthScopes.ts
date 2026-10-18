import type { Role } from "./claims";

// The permissions a connected app can ask for (#798, #1311, #1313), in the order consent
// lists them. `roles` mirrors the policy of the tools behind the scope, for the consent
// screen's "Not allowed for your role"; the API enforces it either way. `atOnce` marks the
// records that have no draft (#1313), which consent says count at once. `wholeFarm` marks a
// scope with a record no flock assignment narrows: inventory purchases.
const PRODUCTION: readonly Role[] = ["Admin", "Manager", "Worker"];

export const SCOPES = [
  { scope: "farm:read", label: "scopeRead", line: "scopeReadLine" },
  { scope: "daily-entries:write", label: "scopeWrite", roles: PRODUCTION },
  { scope: "daily-entries:submit", label: "scopeSubmit", line: "scopeSubmitLine", roles: PRODUCTION },
  { scope: "operations:write", label: "scopeOperations", line: "scopeOperationsLine", roles: PRODUCTION, atOnce: true, wholeFarm: true },
  { scope: "expenses:write", label: "scopeExpenses", roles: ["Admin", "Manager"] as readonly Role[], atOnce: true },
] as const;

export type ScopeEntry = (typeof SCOPES)[number];

export const READ_SCOPE = "farm:read";

export const scopeEntry = (scope: string): ScopeEntry | undefined => SCOPES.find((s) => s.scope === scope);

// The server sorts scopes by name; consent lists them in SCOPES order, and so does Connected apps.
export const scopeRank = (scope: string) => {
  const index = SCOPES.findIndex((s) => s.scope === scope);
  return index === -1 ? Infinity : index;
};

export const allowedFor = (scope: string, role: Role) => {
  const entry = scopeEntry(scope);
  return !entry || !("roles" in entry) || entry.roles.includes(role);
};
