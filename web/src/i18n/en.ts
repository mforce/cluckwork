// The English catalog — the SOURCE OF TRUTH and the fallback (#182). Namespaced
// by area. Keys are camelCase; values are sentence-case UI copy. Adding a key
// here extends the compile-time key type (see types/i18next.d.ts), so a t("typo")
// is a build error.
export const en = {
  common: {
    cancel: "Cancel",
    save: "Save",
    close: "Close",
    delete: "Delete",
    edit: "Edit",
    add: "Add",
    confirm: "Confirm",
    loading: "Loading…",
    retry: "retry",
    required: "Required",
    optional: "Optional",
    actions: "Actions",
    search: "Search",
    all: "All",
    none: "None",
    yes: "Yes",
    no: "No",
  },
  auth: {
    title: "Cluckwork",
    email: "Email",
    password: "Password",
    signIn: "Sign in",
    signingIn: "Signing in…",
    invalidCredentials: "Invalid email or password.",
    tooManyAttempts:
      "Too many sign-in attempts. Please wait a few minutes and try again.",
    apiDown: "Could not sign in. Is the API running?",
  },
  account: {
    preferences: "Preferences",
    language: "Language",
    languageHint: "The language the interface is shown in, just for you.",
  },
  // Keyed by the API's stable validation codes (#45), which contain dots
  // (e.g. "Me.Language.Format"). With keySeparator:false (see init) these are
  // literal flat keys, not nested paths. Filled in Task 4.
  errors: {
    "Me.Language.Format": "Language must be a 2–8 letter code, for example 'en'.",
  },
  // Shared navigation chrome (Task 7, #182) — the FIRST screen-externalization
  // batch (B1). English-only: `nav` is deliberately NOT in TRANSLATED_NAMESPACES
  // (see translations-status.ts), so es/tl fall back to these strings until a
  // native-speaker pass adds the namespace. `navGroups()`/`tabEntries()`
  // (routes/nav.tsx) are PURE FUNCTIONS and cannot call useTranslation, so they
  // carry a typed `labelKey` into this namespace instead of English text; the
  // render sites (AppLayout, BottomNav) translate it. The same labelKeys feed
  // the sidebar, the bottom tab bar, and the More sheet — one source.
  nav: {
    // Section headings (NavGroup.labelKey).
    groupOverview: "Overview",
    groupProduction: "Production",
    groupSalesStock: "Sales & stock",
    groupInsights: "Insights",
    groupSetup: "Setup",
    groupYou: "You",
    groupHelp: "Help",

    // Destination labels (NavEntry.labelKey).
    dashboard: "Dashboard",
    dailyEntry: "Daily entry",
    flocks: "Flocks",
    water: "Water",
    inventory: "Inventory",
    stock: "Stock",
    customers: "Customers",
    sales: "Sales",
    history: "History",
    reports: "Reports",
    expenses: "Expenses",
    farmSettings: "Farm settings",
    grades: "Grades",
    products: "Products",
    users: "Users",
    audit: "Audit",
    export: "Export",
    account: "Account",
    // Reused for BOTH the "Help" destination and the "Help" group heading —
    // identical text, identical meaning, so it stays one key within this
    // namespace (not a `common` atom: nav-local reuse, not cross-screen).
    help: "Help",

    // AppLayout chrome.
    skipToContent: "Skip to main content",
    primaryNavAriaLabel: "Primary",
    signOut: "Sign out",
    farmLoadFailedNeverLoaded:
      "Could not load this farm's settings, so dates follow this device rather than the farm.",
    farmLoadFailedStale:
      "Could not re-read this farm's settings, so what you see here may be out of date.",
    tryAgain: "Try again",
    // Composed with the per-page title (the active nav entry's translated
    // label) to build document.title, e.g. "Dashboard — Cluckwork". The brand
    // word stays as-is, matching FarmBrand's own hardcoded "Cluckwork" fallback.
    titleSuffix: " — Cluckwork",

    // BottomNav chrome.
    tabBarAriaLabel: "Sections",
    moreButton: "More",
    menuTitle: "Menu",
    allSectionsAriaLabel: "All sections",
  },
  // Sales pilot (Task 7, #182) — the worked pattern for the full sweep.
  sales: {
    // Headings
    title: "Sales",
    loading: "Loading…",
    payments: "Payments",
    ordersHeading: "Orders",

    // Buttons
    newOrder: "New order",
    newDraftOrder: "New draft order",
    save: "save",
    cancelEdit: "cancel",
    edit: "edit",
    remove: "remove",
    addLine: "Add line",
    confirmOrderButton: "Confirm order (allocates stock)",
    cancelDraft: "Cancel draft",
    // Intentional screen-specific lowercase variant, distinct from
    // common.close ("Close") — matches this page's lowercase link-styled
    // buttons (edit/remove/open/load more, above/below). Not a dup: same
    // meaning, different case, so it stays here rather than merging (#182).
    close: "close",
    voidPaymentButton: "void",
    recordPayment: "Record payment",
    voidOrderButton: "Void order (returns stock)",
    open: "open",
    loadMore: "load more",

    // Form labels
    customer: "Customer",
    date: "Date",
    product: "Product",
    perLabel: "Per",
    quantity: "Quantity",
    unitPriceWithCurrency: "Unit price ({{code}})",
    method: "Method",
    referenceOptional: "Reference (optional)",
    noteOptional: "Note (optional)",
    amountWithCurrency: "Amount ({{code}})",
    status: "Status",

    // Table headers (shared across the items / payments / orders tables)
    qty: "Qty",
    eggs: "Eggs",
    unitPrice: "Unit price",
    lineTotal: "Line total",
    reference: "Reference",
    amount: "Amount",
    total: "Total",

    // aria-labels
    editQuantityAriaLabel: "Edit quantity",
    editUnitPriceAriaLabel: "Edit unit price",

    // Status-filter options
    // NOTE (#182, Task 5): these four are a TEMPORARY translated duplicate of
    // enums:status.{Draft,Confirmed,Cancelled,Voided} — es/tl carry real
    // translations here, while `enums` is English-only, so the filter dropdown
    // stays on this namespace rather than regressing to English-only labels.
    // Reconcile when status gets its native-translation pass: migrate this
    // dropdown to a then-translated enums:status and delete these four keys.
    allOption: "All",
    statusDraft: "Draft",
    statusConfirmed: "Confirmed",
    statusCancelled: "Cancelled",
    statusVoided: "Voided",

    // Unit picker (the sale unit, e.g. "3 Dozen") — text equals the enum value.
    unitEgg: "Egg",
    unitDozen: "Dozen",
    unitFlat: "Flat",
    unitTray: "Tray",
    unitCarton: "Carton",
    unitCase: "Case",

    // Payment-method picker — text equals the enum value.
    methodCash: "Cash",
    methodCheck: "Check",
    methodCard: "Card",
    methodBankTransfer: "BankTransfer",
    methodMobilePayment: "MobilePayment",
    methodOther: "Other",

    // Misc UI text
    addCustomerFirst: "Add a customer first (Customers page), then create an order.",
    noOrdersMatch: "No orders match.",
    voidingNeedsAdmin: "Voiding needs an admin.",
    voidReasonLabel: "Void reason: {{reason}}",
    orderTotal: "Total: {{amount}}",
    perUnit: "per {{unit}}",
    eggsCount: "({{count}} eggs)",
    // Interleaves JSX (<strong> around "outstanding …") — rendered via <Trans>.
    paymentsSummary: "Paid {{paid}} — <strong>outstanding {{outstanding}}</strong>",

    // Inline validation messages
    enterValidAmount: "Enter a valid amount.",
    noDecimalPlaces: "This currency has no decimal places.",
    atMostDecimals: "At most {{count}} decimal places for this currency.",
    enterAmountGreaterThanZero: "Enter an amount greater than zero.",
    invalidUnitPrice: "Invalid unit price.",
    loadSalesDataFailed: "Could not load sales data. Is the API up?",
    loadOrdersFailed: "Could not load orders.",
    loadPaymentsFailed: "Could not load this order's payments.",

    // Confirm / askReason dialogs (title / body / confirmLabel)
    confirmOrderTitle: "Confirm this order?",
    confirmOrderBody:
      "Stock is allocated from inventory, oldest lots first (FIFO). " +
      "A mistaken confirm can be undone with Void, which returns the stock.",
    confirmOrderConfirmLabel: "Confirm order",
    cancelDraftTitle: "Cancel this draft?",
    cancelDraftBody: "The order becomes cancelled and can no longer be edited or confirmed.",
    voidPaymentTitle: "Void this payment?",
    voidPaymentBody: "The order's outstanding amount grows back by the payment's value.",
    voidPaymentConfirmLabel: "Void payment",
    voidOrderTitle: "Void this confirmed order?",
    voidOrderBody: "The allocated stock returns to the exact egg lots it came from.",
    voidOrderConfirmLabel: "Void order",

    // Templated success messages
    orderConfirmed: "Order {{ref}} confirmed — stock allocated (FIFO).",
    draftOrderCancelled: "Draft order cancelled.",
    paymentRecorded: "Payment recorded.",
    paymentVoided: "Payment voided — the outstanding amount grew back.",
    orderVoided: "Order {{ref}} voided — stock returned to inventory.",
  },
  // Closed-vocabulary labels (#182, Task 4). Consumed ONLY through the typed
  // helpers in enums.ts — never a raw t("enums:status." + value). Keys are FLAT
  // "family.RawValue" strings (keySeparator:false, see index.ts): the suffix is
  // the EXACT wire value the API sends, so the helper's Record can map value →
  // key mechanically and the key is self-documenting. English-only for now —
  // `enums` is deliberately NOT in TRANSLATED_NAMESPACES, so es/tl fall back to
  // these strings until a native enum-translation pass lands.
  //
  // Labels are chosen to be TEXT-PRESERVING on retrofit (Task 5+) — a screen
  // wired to a helper keeps its current text — EXCEPT at two sites the retrofit
  // changes DELIBERATELY (its reviewer must eyeball them):
  //   1. Dashboard.tsx (`<StatusBadge status={e.status} />`, no label) shows a
  //      ManagerAdjusted entry RAW today; statusLabel makes it read "Adjusted" —
  //      the text HistoryPage already shows for that state via its own bespoke
  //      `<span className="badge badge-warn">Adjusted</span>` (HistoryPage.tsx
  //      249-250), NOT a StatusBadge label.
  //   2. UsersPage.tsx role table cell renders raw `{u.role}` = "ReadOnly";
  //      roleLabel makes it read "Read-only" (matching the picker option).
  // Payment method and sale unit are intentionally absent — they belong to the
  // TRANSLATED `sales` namespace (sales:method*/unit*, which carry es/tl), and
  // duplicating them here (English-only) would regress that coverage.
  enums: {
    // status — StatusBadge's STATUS_VALUES vocabulary. Identity except
    // ManagerAdjusted, whose pill reads "Adjusted".
    "status.Active": "Active",
    "status.Inactive": "Inactive",
    "status.Draft": "Draft",
    "status.Submitted": "Submitted",
    "status.Locked": "Locked",
    "status.ManagerAdjusted": "Adjusted",
    "status.Voided": "Voided",
    "status.Confirmed": "Confirmed",
    "status.Shipped": "Shipped",
    "status.Invoiced": "Invoiced",
    "status.Cancelled": "Cancelled",
    "status.Depleted": "Depleted",
    "status.Archived": "Archived",

    // role (UsersPage) — Roles.Assignable + Worker. "ReadOnly" labelled
    // "Read-only" (matches the picker option); the raw u.role table column and
    // the picker's "Admin (owner)" hint are noted in the report as retrofit
    // discrepancies.
    "role.Worker": "Worker",
    "role.Admin": "Admin",
    "role.Manager": "Manager",
    "role.Sales": "Sales",
    "role.ReadOnly": "Read-only",

    // water source (WaterPage picker) — WaterSource enum.
    "waterSource.Well": "Well",
    "waterSource.Municipal": "Municipal",
    "waterSource.Tank": "Tank",
    "waterSource.Other": "Other",

    // water unit (WaterPage picker) — WaterUsage.AllowedUnits, a fixed 2-value
    // set. Labels are the unit symbols, unchanged.
    "waterUnit.L": "L",
    "waterUnit.gal": "gal",

    // grade type (GradesPage picker) — EggGradeType enum.
    "gradeType.Size": "Size",
    "gradeType.Quality": "Quality",
    "gradeType.Custom": "Custom",

    // inventory category (InventoryPage picker) — InventoryCategory enum.
    // "EquipmentPart" kept as one word to match current display.
    "inventoryCategory.Feed": "Feed",
    "inventoryCategory.Supplement": "Supplement",
    "inventoryCategory.Additive": "Additive",
    "inventoryCategory.Medication": "Medication",
    "inventoryCategory.Vaccine": "Vaccine",
    "inventoryCategory.Packaging": "Packaging",
    "inventoryCategory.Bedding": "Bedding",
    "inventoryCategory.Sanitation": "Sanitation",
    "inventoryCategory.EquipmentPart": "EquipmentPart",
    "inventoryCategory.Other": "Other",

    // inventory movement type (InventoryPage ledger) — InventoryMovementType
    // enum. The ledger shows the raw server value, so the full enum is covered
    // (the adjust picker only offers Adjustment/Discard).
    "inventoryMovement.Purchase": "Purchase",
    "inventoryMovement.Usage": "Usage",
    "inventoryMovement.Adjustment": "Adjustment",
    "inventoryMovement.Discard": "Discard",

    // flock (bird) movement type (FlocksPage ledger) — BirdMovementType enum.
    // Mortality is system-generated (from daily-entry deaths); the record picker
    // only offers Cull/Adjustment.
    "flockMovement.Mortality": "Mortality",
    "flockMovement.Cull": "Cull",
    "flockMovement.Adjustment": "Adjustment",

    // egg stock movement type (StockPage lot ledger) — EggMovementType enum.
    // "InternalUse" kept as one word to match current display.
    "stockMovement.Production": "Production",
    "stockMovement.Sale": "Sale",
    "stockMovement.Adjustment": "Adjustment",
    "stockMovement.Discard": "Discard",
    "stockMovement.InternalUse": "InternalUse",
    "stockMovement.Transfer": "Transfer",
    "stockMovement.Reconciliation": "Reconciliation",
    "stockMovement.Void": "Void",

    // unit system (SettingsPage picker) — UnitSystem enum.
    "unitSystem.Metric": "Metric",
    "unitSystem.Imperial": "Imperial",

    // weekday (SettingsPage week-start picker) — standalone day-name labels.
    "weekday.Sunday": "Sunday",
    "weekday.Monday": "Monday",
    "weekday.Tuesday": "Tuesday",
    "weekday.Wednesday": "Wednesday",
    "weekday.Thursday": "Thursday",
    "weekday.Friday": "Friday",
    "weekday.Saturday": "Saturday",
  },
} as const;

export type Resources = typeof en;
