// The permissions a connected app can ask for (#798, #1311, #1313), in the order consent
// lists them. Which of them the person's role can use comes from the server (#1326).
// `atOnce` marks the records that have no draft (#1313), which consent says count at once.
// `wholeFarm` marks a scope with a record no flock assignment narrows: inventory purchases.
export const SCOPES = [
  { scope: "farm:read", label: "scopeRead", line: "scopeReadLine" },
  { scope: "daily-entries:write", label: "scopeWrite" },
  { scope: "daily-entries:submit", label: "scopeSubmit", line: "scopeSubmitLine" },
  { scope: "operations:write", label: "scopeOperations", line: "scopeOperationsLine", atOnce: true, wholeFarm: true },
  { scope: "expenses:write", label: "scopeExpenses", atOnce: true },
] as const;

export type ScopeEntry = (typeof SCOPES)[number];

export const READ_SCOPE = "farm:read";

export const scopeEntry = (scope: string): ScopeEntry | undefined => SCOPES.find((s) => s.scope === scope);

// The server sorts scopes by name; consent lists them in SCOPES order, and so does Connected apps.
export const scopeRank = (scope: string) => {
  const index = SCOPES.findIndex((s) => s.scope === scope);
  return index === -1 ? Infinity : index;
};
