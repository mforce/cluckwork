// #657 — the in-app glossary as data. One entry per term; the Help page
// renders them grouped and alphabetised in the active language, each with a
// stable anchor (`/help#glossary-egg-lot`) that GlossaryLink and the rail
// point at.
//
// `spec` names the specs/product/GLOSSARY.md term this entry is the curated
// subset of. helpGlossary.test.ts walks GLOSSARY.md and fails on any entry
// whose spec term no longer exists there, which is how the two glossaries stay
// in step without a hand-maintained checklist. The catalog keys are
// `glossary<Key>Term` / `glossary<Key>Def`; the same test fails on a catalog
// row with no entry here, and on an entry with no row in en, es or tl.

export const GLOSSARY_GROUPS = [
  { key: "gettingAround", labelKey: "glossaryGroupGettingAround" },
  { key: "signingIn", labelKey: "glossaryGroupSigningIn" },
  { key: "flocksEntry", labelKey: "glossaryGroupFlocksEntry" },
  { key: "eggsStock", labelKey: "glossaryGroupEggsStock" },
  { key: "salesMoney", labelKey: "glossaryGroupSalesMoney" },
  { key: "supplies", labelKey: "glossaryGroupSupplies" },
  { key: "farm", labelKey: "glossaryGroupFarm" },
] as const;

export type GlossaryGroupKey = (typeof GLOSSARY_GROUPS)[number]["key"];

const ENTRIES = [
  // Getting around
  { key: "Navigation", group: "gettingAround", spec: "Navigation" },
  { key: "PageLoading", group: "gettingAround", spec: "Page loading" },
  { key: "SearchablePicker", group: "gettingAround", spec: "Searchable picker", rich: true },
  { key: "SomethingWentWrongScreen", group: "gettingAround", spec: "\"Something went wrong\" screen" },
  { key: "InstallToHomeScreen", group: "gettingAround", spec: "Install to home screen", rich: true },
  { key: "NewVersionReady", group: "gettingAround", spec: "New version is ready" },
  { key: "UiLanguage", group: "gettingAround", spec: "UI language" },
  // Signing in & who can do what
  { key: "FarmCode", group: "signingIn", spec: "Farm code", rich: true },
  { key: "LoginEmail", group: "signingIn", spec: "Login email" },
  { key: "FarmProvisioning", group: "signingIn", spec: "Farm provisioning" },
  { key: "TooManySignInAttempts", group: "signingIn", spec: "Auth rate limiting" },
  { key: "ForcedReauth", group: "signingIn", spec: "Session tokens" },
  { key: "StepUpAuth", group: "signingIn", spec: "Step-up authentication" },
  { key: "Roles", group: "signingIn", spec: "Roles" },
  { key: "FlockScoping", group: "signingIn", spec: "Flock scoping" },
  { key: "DisabledUser", group: "signingIn", spec: "Disabled user" },
  // Flocks & daily entry
  { key: "CaptureStatus", group: "flocksEntry", spec: "Capture status" },
  { key: "DailyEntry", group: "flocksEntry", spec: "Daily entry" },
  { key: "OperationalDay", group: "flocksEntry", spec: "Operational day" },
  { key: "LockedEntry", group: "flocksEntry", spec: "Daily entry lifecycle" },
  { key: "AdjustEntry", group: "flocksEntry", spec: "Daily entry lifecycle" },
  { key: "VoidEntry", group: "flocksEntry", spec: "Daily entry lifecycle" },
  { key: "Cull", group: "flocksEntry", spec: "Bird movement" },
  { key: "Mortality", group: "flocksEntry", spec: "Bird movement" },
  { key: "Deplete", group: "flocksEntry", spec: "Flock lifecycle" },
  { key: "Archive", group: "flocksEntry", spec: "Flock lifecycle" },
  // Eggs, grades & stock
  { key: "EggLot", group: "eggsStock", spec: "Egg lot" },
  { key: "Grade", group: "eggsStock", spec: "Egg grade" },
  { key: "EggMovementLedger", group: "eggsStock", spec: "Egg movement ledger" },
  { key: "StockWriteOff", group: "eggsStock", spec: "Stock write-off" },
  { key: "Fifo", group: "eggsStock", spec: "FIFO allocation" },
  { key: "WithdrawalRestriction", group: "eggsStock", spec: "Withdrawal restriction" },
  { key: "Product", group: "eggsStock", spec: "Product" },
  { key: "PackedUnit", group: "eggsStock", spec: "Packed unit" },
  { key: "CountingUnit", group: "eggsStock", spec: "Stepper counting unit" },
  // Sales & money
  { key: "SalesLine", group: "salesMoney", spec: "Sales line" },
  { key: "ConfirmOrder", group: "salesMoney", spec: "Sales order lifecycle" },
  { key: "VoidOrder", group: "salesMoney", spec: "Void" },
  { key: "CancelOrder", group: "salesMoney", spec: "Sales order lifecycle" },
  { key: "WorkerSaleAllocation", group: "salesMoney", spec: "Worker sale allocation policy" },
  { key: "CurrencyLock", group: "salesMoney", spec: "Currency change rule" },
  { key: "TooManyReports", group: "salesMoney", spec: "Report query bounding + concurrency limit" },
  // Feed, water & supplies
  { key: "InventoryItem", group: "supplies", spec: "Inventory item" },
  { key: "InventoryLot", group: "supplies", spec: "Inventory lot" },
  { key: "InventoryMovementLedger", group: "supplies", spec: "Inventory movement ledger" },
  { key: "FeedUsage", group: "supplies", spec: "Feed usage" },
  { key: "WaterUsage", group: "supplies", spec: "Water usage" },
  { key: "AdjustmentDiscard", group: "supplies", spec: "Adjustment / Discard" },
  // Farm settings & branding
  { key: "FarmSettings", group: "farm", spec: "Farm settings" },
  { key: "FarmLogo", group: "farm", spec: "Farm logo" },
  { key: "FarmBanner", group: "farm", spec: "Farm banner" },
  { key: "FarmPalette", group: "farm", spec: "Farm palette" },
] as const satisfies readonly { key: string; group: GlossaryGroupKey; spec: string; rich?: true }[];

export type GlossaryKey = (typeof ENTRIES)[number]["key"];

export interface GlossaryEntry {
  key: GlossaryKey;
  group: GlossaryGroupKey;
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
