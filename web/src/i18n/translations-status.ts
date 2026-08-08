import type { en } from "./en";

// Namespaces where es/tl are maintained in lockstep with en (#182,
// translate-now model). Every swept screen ships machine-drafted es/tl inline
// (marked pending native review) and is listed here. Anything NOT listed falls
// back to English (fallbackLng, see index.ts). New screens must add their
// namespace to en.ts AND es.ts AND tl.ts AND this list in the same PR.
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
  "nav",
  "numberField",
  "errorBoundary",
  "themeToggle",
  "useConfirm",
  "pwa",
  "dailyEntry",
  "dashboard",
  "feed",
  "water",
  "grades",
  "inventory",
  "products",
  "stock",
  "flocks",
  "help",
] as const satisfies readonly (keyof typeof en)[];

export type TranslatedNamespace = (typeof TRANSLATED_NAMESPACES)[number];
