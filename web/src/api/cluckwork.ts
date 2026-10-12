// Typed wrappers over the Cluckwork JSON API (mirrors the endpoint DTOs).
// #1215 — split by API area; the barrel re-exports every name, so importers
// keep importing from ./cluckwork and tests may keep mocking it.
export * from "./cluckwork/shared";
export * from "./cluckwork/eggGrades";
export * from "./cluckwork/flocks";
export * from "./cluckwork/dailyEntries";
export * from "./cluckwork/stock";
export * from "./cluckwork/sales";
export * from "./cluckwork/account";
export * from "./cluckwork/inventory";
export * from "./cluckwork/users";
export * from "./cluckwork/audit";
export * from "./cluckwork/catalog";
export * from "./cluckwork/exports";
export * from "./cluckwork/reports";
export * from "./cluckwork/expenses";
