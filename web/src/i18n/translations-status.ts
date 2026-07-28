import type { en } from "./en";

// Namespaces where es/tl are maintained in lockstep with en (#182,
// English-first model). A screen added AFTER the English-first cutover ships
// its strings to en.ts ONLY — es/tl are not required to add the namespace at
// all, and simply fall back to English (fallbackLng, see index.ts) for it
// until a native-speaker translation pass lands and adds the namespace here.
//
// catalogParity.test.ts:
// - enforces key-set/non-empty/placeholder/tag parity between en and es/tl
//   ONLY for the namespaces listed here (not every namespace en happens to
//   have);
// - separately asserts es/tl carry NO namespace outside this list, so an
//   accidental machine-draft of a deferred namespace still fails the build.
//
// `satisfies` (not `as`) so a typo'd or renamed namespace here is a
// compile-time error against the real en.ts shape, while `as const` keeps the
// literal string types for TRANSLATED_NAMESPACES consumers.
export const TRANSLATED_NAMESPACES = [
  "common",
  "auth",
  "account",
  "errors",
  "sales",
  "enums",
  "settings",
  "users",
  "expenses",
  "customers",
  "history",
  "reports",
  "audit",
  "export",
] as const satisfies readonly (keyof typeof en)[];

export type TranslatedNamespace = (typeof TRANSLATED_NAMESPACES)[number];
