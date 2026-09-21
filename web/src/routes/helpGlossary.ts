// The in-app glossary is data: one entry per term, in the same order as the
// locale catalogs, with a
// stable anchor (`/help#glossary-egg-lot`) that GlossaryLink and the rail
// point at.
//
// `spec` names the specs/product/GLOSSARY.md term this entry is the curated
// subset of. helpGlossary.test.ts walks GLOSSARY.md and fails on any entry
// whose spec term no longer exists there, which is how the two glossaries stay
// in step without a hand-maintained checklist. The catalog keys are
// `glossary<Key>Term` / `glossary<Key>Def`; the same test fails on a catalog
// row with no entry here, and on an entry with no row in en, es or tl.

const ENTRIES = [
  { key: "Navigation", spec: "Navigation" },
  { key: "PageLoading", spec: "Page loading" },
  { key: "SearchablePicker", spec: "Searchable picker", rich: true },
  { key: "OperationalDay", spec: "Operational day" },
  { key: "InstallToHomeScreen", spec: "Install to home screen", rich: true },
  { key: "NewVersionReady", spec: "New version is ready" },
  { key: "FarmCode", spec: "Farm code", rich: true },
  { key: "LoginEmail", spec: "Login email" },
  { key: "FarmProvisioning", spec: "Farm provisioning" },
  { key: "TooManySignInAttempts", spec: "Auth rate limiting" },
  { key: "ForcedReauth", spec: "Session tokens" },
  { key: "TooManyReports", spec: "Report query bounding + concurrency limit" },
  { key: "StepUpAuth", spec: "Step-up authentication" },
  { key: "SomethingWentWrongScreen", spec: "\"Something went wrong\" screen" },
  { key: "DailyEntry", spec: "Daily entry" },
  { key: "CaptureStatus", spec: "Capture status" },
  { key: "EggLot", spec: "Egg lot" },
  { key: "Grade", spec: "Egg grade" },
  { key: "EggMovementLedger", spec: "Egg movement ledger" },
  { key: "StockWriteOff", spec: "Stock write-off" },
  { key: "Fifo", spec: "FIFO allocation" },
  { key: "WorkerSaleAllocation", spec: "Worker sale allocation policy" },
  { key: "Cull", spec: "Bird movement" },
  { key: "Mortality", spec: "Bird movement" },
  { key: "Deplete", spec: "Flock lifecycle" },
  { key: "Archive", spec: "Flock lifecycle" },
  { key: "WithdrawalRestriction", spec: "Withdrawal restriction" },
  { key: "Product", spec: "Product" },
  { key: "PackedUnit", spec: "Packed unit" },
  { key: "CountingUnit", spec: "Stepper counting unit" },
  { key: "SalesLine", spec: "Sales line" },
  { key: "ConfirmOrder", spec: "Sales order lifecycle" },
  { key: "VoidOrder", spec: "Void" },
  { key: "CancelOrder", spec: "Sales order lifecycle" },
  { key: "ListPrice", spec: "List price" },
  { key: "Discount", spec: "Discount" },
  { key: "DiscountReason", spec: "Discount reason" },
  { key: "DiscountCeiling", spec: "Discount ceiling" },
  { key: "AboveList", spec: "Above list" },
  { key: "Outstanding", spec: "Outstanding balance" },
  { key: "InventoryItem", spec: "Inventory item" },
  { key: "InventoryLot", spec: "Inventory lot" },
  { key: "InventoryMovementLedger", spec: "Inventory movement ledger" },
  { key: "WaterUsage", spec: "Water usage" },
  { key: "FeedUsage", spec: "Feed usage" },
  { key: "AdjustmentDiscard", spec: "Adjustment / Discard" },
  { key: "Roles", spec: "Roles" },
  { key: "FlockScoping", spec: "Flock scoping" },
  { key: "LockedEntry", spec: "Daily entry lifecycle" },
  { key: "AdjustEntry", spec: "Daily entry lifecycle" },
  { key: "VoidEntry", spec: "Daily entry lifecycle" },
  { key: "FarmSettings", spec: "Farm settings" },
  { key: "CurrencyLock", spec: "Currency change rule" },
  { key: "FarmLogo", spec: "Farm logo" },
  { key: "FarmBanner", spec: "Farm banner" },
  { key: "FarmPalette", spec: "Farm palette" },
  { key: "UiLanguage", spec: "UI language" },
  { key: "DisabledUser", spec: "Disabled user" },
] as const satisfies readonly { key: string; spec: string; rich?: true }[];

export type GlossaryKey = (typeof ENTRIES)[number]["key"];

export interface GlossaryEntry {
  key: GlossaryKey;
  spec: string;
  rich?: true;
  // Anchor id on the Help page: the key in kebab case under a fixed prefix,
  // so a link minted anywhere in the app stays valid as long as the key does.
  id: string;
  termKey: `glossary${GlossaryKey}Term`;
  defKey: `glossary${GlossaryKey}Def`;
}

const kebab = (key: string) => key.replace(/([a-z0-9])([A-Z])/g, "$1-$2").toLowerCase();

export const GLOSSARY: readonly GlossaryEntry[] = ENTRIES.map((e) => ({
  ...e,
  id: `glossary-${kebab(e.key)}`,
  termKey: `glossary${e.key}Term`,
  defKey: `glossary${e.key}Def`,
}));

export function glossaryEntry(key: GlossaryKey): GlossaryEntry {
  const entry = GLOSSARY.find((e) => e.key === key);
  if (entry === undefined) throw new Error(`No glossary entry for ${key}`);
  return entry;
}
