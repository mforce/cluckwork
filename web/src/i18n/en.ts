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
    // Distinct from `groupHelp` above: this is the "Help" DESTINATION's own
    // labelKey, just like every other entry pairs with its group's `group*`
    // heading key (e.g. `groupSetup`/farmSettings). The rendered text happens
    // to coincide with the group heading, but the two keys are independent.
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
  // Shared primitives (Task 8, #182, batch B1) — Dialog's close button reuses
  // `common.close` (no new key needed); these two components have genuinely
  // new copy of their own. English-only for now: neither namespace is in
  // TRANSLATED_NAMESPACES (see translations-status.ts), so es/tl fall back to
  // these exact strings until a native-speaker pass adds them — same
  // treatment as `nav` above.
  numberField: {
    // Interpolated with the caller-supplied field name (e.g. "total eggs"),
    // which is domain text passed in via NumberField's `label` prop, not
    // translated here.
    increaseLabel: "Increase {{label}}",
    decreaseLabel: "Decrease {{label}}",
  },
  errorBoundary: {
    title: "Something went wrong",
    screenBody:
      "This screen ran into a problem and couldn’t finish loading. Anything you’d already saved is safe, but anything you were still typing here may need to be entered again. The rest of the app still works.",
    appBody:
      "The app ran into a problem and couldn’t finish loading. Reloading usually clears it.",
    reload: "Reload",
    backToDashboard: "Back to the dashboard",
    detailsSummary: "Error details",
  },
  // The last three shared components (Task 9, #182, batch B1). Cancel reuses
  // common.cancel (see useConfirm below) rather than duplicating it here.
  // English-only for now, same treatment as `nav`/`numberField`/`errorBoundary`
  // above: neither namespace is in TRANSLATED_NAMESPACES, so es/tl fall back to
  // these strings until a native-speaker pass adds them.
  themeToggle: {
    switchToLightMode: "Switch to light mode",
    switchToNightMode: "Switch to night mode",
    light: "Light",
    night: "Night",
  },
  useConfirm: {
    reasonLabel: "Reason *",
    reasonRequired: "A reason is required.",
  },
  // The service-worker "update ready" banner (UpdatePrompt.tsx, src/pwa) — named
  // for the directory rather than the component, since it's the one place PWA
  // chrome lives today.
  pwa: {
    updateAvailable: "A new version of Cluckwork is ready.",
    reload: "Reload",
    reloading: "Reloading…",
    later: "Later",
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
  // Daily entry capture screen (Task 11, #182, batch B2). English-only for
  // now, same treatment as nav/numberField/errorBoundary/themeToggle/
  // useConfirm/pwa above: `dailyEntry` is deliberately NOT in
  // TRANSLATED_NAMESPACES, so es/tl fall back to these strings until a
  // native-speaker pass adds the namespace. The one closed-vocabulary display
  // on this screen (the entry-locked banner's status word) goes through the
  // `enums` `statusLabel` helper, not a key here — see DailyEntryPage.tsx.
  dailyEntry: {
    title: "Daily entry",

    // Imperative messages (event handlers / promise callbacks — see
    // CONTRIBUTING-i18n.md's imperative i18n.t() pattern).
    loadFlocksGradesFailed: "Could not load flocks/grades. Is the API up?",
    deepLinkUnavailable:
      "This edit link points at a flock or date that is no longer available — using the usual defaults instead.",

    // "Editing draft" badge — a bespoke StatusBadge label, not an enum display.
    editingDraftBadge: "Editing draft",

    // Flock + date context row
    flockLabel: "Flock",
    noFlocksYetOption: "— no flocks yet —",
    depletedFlockSuffix: " — depleted, backfill only",
    dateLabel: "Date",
    newFlockButton: "+ new flock",

    // New-flock dialog (F131)
    newFlockDialogTitle: "New flock",
    nameLabel: "Name",
    breedLabel: "Breed",
    placedLabel: "Placed",
    birdsLabel: "Birds",
    createFlockButton: "Create flock",

    // Locked-day / prefill-failure banners. `status` is interpolated with the
    // ALREADY-LABELLED (statusLabel), lowercased value — never the raw wire
    // status.
    entryLockedBanner:
      "This day is already {{status}} — its egg lots exist. "
      + "Corrections are made from History (admins: adjust or void).",
    prefillFailedBanner:
      "Could not check whether this day already has an entry — saving is "
      + "blocked so existing data isn't overwritten.",

    // Step headings (F134's two-step layout)
    stepLabel: "Step {{n}}",
    stepOfTotal: "of 2:",
    eggCountsHeading: "Egg counts",
    gradingHeading: "Grading",

    // Count field labels — reused lowercased (see g.name.toLowerCase() for
    // grades, same pattern) as NumberField's aria-label `label` prop, so there
    // is no separate lowercase key to keep in sync.
    totalEggsLabel: "Total eggs",
    crackedLabel: "Cracked",
    dirtyLabel: "Dirty",
    discardedLabel: "Discarded",
    mortalityLabel: "Mortality",

    // Reconciliation readouts (counts pane)
    countsExceedTotalMessage:
      "Cracked + dirty + discarded ({{losses}}) exceed total eggs ({{total}}).",
    sellableLabel: "Sellable",
    sellableFormula: "{{total}} − {{cracked}} − {{dirty}} − {{discarded}}",
    deactivatedGradeSuffix: " (deactivated)",

    // F134 remainder-assignment gesture (grading pane)
    takeRemainderAriaLabel: "Put all {{count}} remaining in {{grade}}",
    takeRemainderButton: "+{{count}}",
    armAriaLabel: "Choose a grade for the remaining {{count}}",
    disarmAriaLabel: "Cancel choosing a grade",
    armButton: "put all in…",
    disarmButton: "pick a grade…",

    // The `grading` derived object's copy (chip + pinned footer)
    fixCountsFirst: "Fix the counts first",
    fixCountsShort: "fix the counts",
    overSellableCount: "over the sellable count",
    overShort: "over",
    gradedDayAddsUp: "graded — the day adds up",
    allGradedShort: "all graded",
    leftToGrade: "left to grade",
    leftShort: "left",

    // Pinned footer (phone-only summary + saves)
    countsExceedFooterMessage: "Losses exceed the total — fix the counts",
    sellableWord: "sellable",
    saveDraftButton: "Save draft",
    submitButton: "Save & submit (creates egg lots)",

    // Submit confirmation dialog (one-way action, #59)
    confirmSubmitTitle: "Submit this day?",
    confirmSubmitBody:
      "Egg lots are created and the entry can no longer be edited. "
      + "Corrections after this need a manager adjustment.",
    confirmSubmitLabel: "Submit day",

    // Save-result messages
    submittedMessage: "Submitted — {{count}} egg lot(s) created.",
    draftSavedMessage: "Draft saved.",
  },
  // Landing page — today's per-flock production, stock by grade, and recent
  // sales (Task 12, #182, batch B2). English-only for now, same treatment as
  // nav/numberField/errorBoundary/themeToggle/useConfirm/pwa/dailyEntry above:
  // `dashboard` is deliberately NOT in TRANSLATED_NAMESPACES, so es/tl fall
  // back to these strings until a native-speaker pass adds the namespace. The
  // two status pills on this screen (today's per-flock entry status, and each
  // recent order's status) go through the `enums` `statusLabel` helper, not a
  // key here — see Dashboard.tsx. Wiring the per-flock entry pill is an
  // INTENTIONAL harmonization, not text-preserving: a ManagerAdjusted entry
  // used to render raw ("ManagerAdjusted") and now reads "Adjusted", matching
  // HistoryPage's own bespoke badge for the same state (see the `enums`
  // header comment below).
  dashboard: {
    title: "Dashboard",

    // Imperative message (promise callback — see CONTRIBUTING-i18n.md's
    // imperative i18n.t() pattern): every parallel fetch failed.
    loadFailed: "Could not load dashboard. Is the API up?",
    // Shared across all three panels — each degrades independently on its own
    // failed fetch.
    panelLoadError: "Could not load.",

    // Stat row
    statEggsCollectedToday: "Eggs collected today",
    statEggsAvailable: "Eggs available",
    statActiveFlocks: "Active flocks",

    // "Today" panel (per-flock production)
    todayPanelTitle: "Today",
    noFlocksMessage: "No flocks yet — create one on the Daily entry page.",
    flockHeader: "Flock",
    statusHeader: "Status",
    eggsHeader: "Eggs",
    lossesHeader: "Losses",
    mortalityHeader: "Mortality",
    // A flock with no entry yet — a bespoke warn badge, not a StatusBadge.
    noEntryBadge: "no entry",

    // "Stock" panel (by grade)
    stockPanelTitle: "Stock",
    noStockMessage: "No stock yet — record and submit a daily entry.",
    gradeHeader: "Grade",
    availableHeader: "Available",
    restrictedHeader: "Restricted",
    eggsAvailableMessage: "{{count}} eggs available.",

    // "Recent sales" panel (hidden for ReadOnly/Denied, #127)
    salesPanelTitle: "Recent sales",
    noOrdersMessage: "No orders yet.",
    refHeader: "Ref",
    customerHeader: "Customer",
    totalHeader: "Total",
  },
  // Water usage capture + correction screen (Task 13, #182, batch B2).
  // English-only for now, same treatment as nav/numberField/errorBoundary/
  // themeToggle/useConfirm/pwa/dailyEntry/dashboard above: `water` is
  // deliberately NOT in TRANSLATED_NAMESPACES, so es/tl fall back to these
  // strings until a native-speaker pass adds the namespace. The two
  // closed-vocabulary displays on this screen — the Source picker + table
  // cell, and the Unit picker + table cell + the quantity label's unit
  // suffix — go through the `enums` `waterSourceLabel`/`waterUnitLabel`
  // helpers, not a key here — see WaterPage.tsx. Both families are
  // identity-labelled in English today, so wiring them changes nothing
  // visible (confirmed in the report).
  water: {
    title: "Water",

    // Imperative messages (event handlers / promise callbacks — see
    // CONTRIBUTING-i18n.md's imperative i18n.t() pattern).
    loadFlocksFailed: "Could not load flocks. Is the API up?",
    loadRecordsFailed: "Could not load water records.",
    loadMoreFailed: "Could not load more.",
    concurrentEditError:
      "This record was just changed elsewhere — reload the list and retry.",

    intro:
      "Record what each flock drank — a direct amount, or meter readings (the "
      + "amount is the meter delta). Records can be corrected later; flock and "
      + "date are fixed.",

    // Capture form labels
    flockLabel: "Flock",
    depletedFlockSuffix: " — depleted, backfill only",
    dateLabel: "Date",
    sourceLabel: "Source",
    unitLabel: "Unit",
    fromMeterReadingsLabel: "from meter readings",
    meterStartLabel: "Meter start",
    meterEndLabel: "Meter end",
    // {{unit}} is the ALREADY-LABELLED (waterUnitLabel) current unit — never
    // the raw wire value.
    quantityLabelWithUnit: "Quantity ({{unit}})",
    noteLabel: "Note",

    // Capture form buttons
    recordWaterButton: "Record water",
    saveCorrectionButton: "Save correction",
    cancelEditButton: "cancel edit",

    // Inline validation messages
    quantityMustBePositive: "Quantity must be a positive number.",
    bothMeterReadingsRequired: "Both meter readings are required.",

    // Save-result messages
    recordedMessage: "Water recorded.",
    recordCorrectedMessage: "Water record corrected.",

    // Records list — filters
    recordsHeading: "Records",
    fromLabel: "From",
    toLabel: "To",
    noRecordsMatch: "No water records match.",

    // Records table
    dateHeader: "Date",
    flockHeader: "Flock",
    amountHeader: "Amount",
    sourceHeader: "Source",
    metersHeader: "Meters",
    noteHeader: "Note",
    correctButton: "correct",
    loadMoreButton: "load more",
  },
  // Egg grade catalog admin screen (Task 14, #182, batch B2 — the last B2
  // screen). English-only for now, same treatment as nav/numberField/
  // errorBoundary/themeToggle/useConfirm/pwa/dailyEntry/dashboard/water above:
  // `grades` is deliberately NOT in TRANSLATED_NAMESPACES, so es/tl fall back
  // to these strings until a native-speaker pass adds the namespace. The one
  // closed-vocabulary display on this screen — the Type picker + table cell —
  // goes through the `enums` `gradeTypeLabel` helper, not a key here — see
  // GradesPage.tsx. Size/Quality/Custom are identity-labelled in English
  // today, so wiring it changes nothing visible (confirmed in the report).
  // Grade NAMES (`g.name`) are free-form farm data, not client copy, and stay
  // raw — never routed through the catalog.
  grades: {
    // Titles. `title` is the loaded heading ("Egg grades"); `loadingTitle` is
    // the shorter pre-existing heading the two early-return states (load
    // error / still loading) used before this sweep — a pre-existing
    // inconsistency with the loaded heading, preserved verbatim rather than
    // "fixed" as a drive-by (not this task's job).
    title: "Egg grades",
    loadingTitle: "Grades",

    // Imperative message (promise callback — see CONTRIBUTING-i18n.md's
    // imperative i18n.t() pattern).
    loadGradesFailed: "Could not load grades. Is the API up?",

    intro:
      "Saleable grades appear in daily-entry and order pickers. Deactivating "
      + "a grade removes it from pickers; existing stock and history are "
      + "unaffected.",

    // Buttons. `newGradeButton` (the page's action button) and
    // `newGradeDialogTitle` happen to share English text today but are
    // separate keys, one per UI role — same treatment as
    // dailyEntry:newFlockButton/newFlockDialogTitle.
    newGradeButton: "New grade",
    newGradeDialogTitle: "New grade",
    editGradeDialogTitle: "Edit grade",
    addGradeButton: "Add grade",
    // Lowercase link-styled row actions, matching sales:edit/water:correctButton.
    editButton: "edit",
    deactivateButton: "deactivate",
    activateButton: "activate",

    // Create-dialog form labels.
    nameLabel: "Name *",
    typeLabel: "Type",
    sortLabel: "Sort",
    saleableLabel: "saleable",
    // Edit-dialog's Name label carries no required marker: the row's save
    // used to be a plain button, so native constraint validation never ran on
    // this field, and the dialog keeps that parity (see GradesPage.test.tsx).
    editNameLabel: "Name",

    // Table headers — separate keys from the form labels above even where the
    // English text coincides (same treatment as water:flockLabel/flockHeader).
    nameHeader: "Name",
    typeHeader: "Type",
    sortHeader: "Sort",
    saleableHeader: "Saleable",
    statusHeader: "Status",

    // Saleable column's "yes" badge — lowercase, distinct from common.yes
    // ("Yes"); same case-sensitive-variant treatment as sales:close.
    saleableYesBadge: "yes",
  },
  // Feed & inventory catalog + receiving/usage/correction screen (Task 16,
  // #182, batch B3 — the biggest B3 screen). English-only for now, same
  // treatment as nav/numberField/errorBoundary/themeToggle/useConfirm/pwa/
  // dailyEntry/dashboard/water/grades above: `inventory` is deliberately NOT
  // in TRANSLATED_NAMESPACES, so es/tl fall back to these strings until a
  // native-speaker pass adds the namespace. Two closed-vocabulary displays go
  // through `enums` helpers rather than a key here — see InventoryPage.tsx:
  //   1. Category — the New/edit item picker option AND the items-table
  //      Category cell both use `inventoryCategoryLabel`; the "not feedable"
  //      message also interpolates the labelled category, never the raw
  //      wire value.
  //   2. The movement LEDGER's Type cell uses `inventoryMovementLabel` (the
  //      raw wire value, identity-labelled in English). The Correct-stock
  //      dialog's Type PICKER is deliberately NOT wired to that helper: its
  //      two options show decorated screen copy ("Adjustment (±)", "Discard
  //      (write-off)") that isn't the ledger's identity label, so it stays a
  //      plain `inventory` key (adjustTypeAdjustmentOption/
  //      adjustTypeDiscardOption).
  // The Active/Inactive status badge uses `statusLabel` (identity in English);
  // `status=` itself stays the raw wire value. `unitCode`/`unit` and item/lot
  // names are free-form farm DATA, not client copy, and stay raw — never
  // routed through a catalog or enum helper.
  inventory: {
    title: "Feed & inventory",
    intro:
      "Receive stock as purchases; every change lands in the item's movement "
      + "ledger. Recording feed usage against flocks arrives next.",

    // Imperative messages (event handlers / promise callbacks — see
    // CONTRIBUTING-i18n.md's imperative i18n.t() pattern).
    loadInventoryFailed: "Could not load inventory. Is the API up?",
    invalidCostError: "Invalid cost.",
    itemCreatedMessage: "Item created.",
    loadLedgerFailed: "Could not load the movement ledger.",
    quantityMustBePositive: "Quantity must be a positive number.",
    purchaseRecordedMessage: "Purchase recorded — stock received.",
    usageRecordedMessage: "Feed usage recorded — stock drained oldest lots first.",
    adjustQuantityRequired: "Adjustment quantity must be a non-zero number (negative removes stock).",
    adjustReasonRequired: "A reason is required for corrections.",
    correctionRecordedMessage: "Correction recorded in the ledger.",

    // Page-head button + New/edit item dialogs
    newItemButton: "New item",
    newItemDialogTitle: "New inventory item",
    editItemDialogTitle: "Edit item",
    itemNameLabel: "Item name *",
    editItemNameLabel: "Item name",
    categoryLabel: "Category",
    unitLabel: "Unit *",
    editUnitLabel: "Unit",
    defaultCostLabel: "Default cost/unit",
    addItemButton: "Add item",

    // Item panel (opened item)
    itemPanelHeading: "{{name}} — {{quantity}} {{unit}} on hand",
    recordPurchaseButton: "Record purchase",
    recordUsageButton: "Record usage",
    correctStockButton: "Correct stock",
    // {{category}} is the ALREADY-LABELLED (inventoryCategoryLabel) category —
    // never the raw wire value. "Feed, Supplement, and Additive" names the
    // fixed feedable set (mirrors FEEDABLE_CATEGORIES) as screen prose, not a
    // dynamic enum render.
    notFeedableMessage:
      "{{category}} items aren't fed to flocks — usage applies to Feed, "
      + "Supplement, and Additive items only.",
    noFlocksForUsageMessage: "No flocks — usage needs a flock to feed.",
    correctionsNeedAdminMessage: "Stock corrections need an admin.",
    noLotsMessage: "No lots yet — corrections target a received lot.",

    // Record-purchase dialog
    recordPurchaseDialogTitle: "Record purchase — {{name}}",
    receivedLabel: "Received",
    // {{unit}} is the item's free-form unit code (DATA), not an enum label —
    // shared verbatim across the purchase/usage/adjust dialogs.
    quantityLabelWithUnit: "Quantity ({{unit}})",
    unitCostLabel: "Unit cost",
    unitCostWithCurrencyLabel: "Unit cost ({{code}})",
    costPlaceholderItemDefault: "item default",
    costPlaceholderRequired: "required",
    lotNumberLabel: "Lot #",
    expiryLabel: "Expiry",
    noteLabel: "Note",
    recordPurchaseSubmitButton: "Record purchase",

    // Record-usage dialog
    recordUsageDialogTitle: "Record usage — {{name}}",
    flockLabel: "Flock",
    depletedFlockSuffix: " (depleted — backfill only)",
    dateLabel: "Date",
    recordUsageSubmitButton: "Record usage",

    // Correct-stock dialog. The Type picker's two options are DECORATED
    // screen copy, not the ledger's inventoryMovementLabel identity text —
    // see the namespace header comment above.
    correctStockDialogTitle: "Correct stock — {{name}}",
    lotFieldLabel: "Lot",
    typeLabel: "Type",
    adjustTypeAdjustmentOption: "Adjustment (±)",
    adjustTypeDiscardOption: "Discard (write-off)",
    adjustQuantityPlaceholderDiscard: "amount discarded",
    adjustQuantityPlaceholderCorrection: "± correction",
    reasonLabel: "Reason *",
    recordCorrectionButton: "Record correction",

    // Movement ledger table
    ledgerDateHeader: "Date",
    ledgerTypeHeader: "Type",
    ledgerQuantityHeader: "Quantity",
    ledgerNoteHeader: "Note",
    noMovementsMessage: "No movements yet — record a purchase above.",
    closeButton: "close",

    // Items table
    nameHeader: "Name",
    categoryHeader: "Category",
    onHandHeader: "On hand",
    defaultCostHeader: "Default cost",
    statusHeader: "Status",
    openButton: "open",
    editButton: "edit",
    deactivateButton: "deactivate",
    activateButton: "activate",
  },
  // Product catalog — what the farm sells — + packed-unit (egg-unit)
  // conversions admin screen (Task 17, #182, batch B3 — the last B3 screen).
  // English-only for now, same treatment as nav/numberField/errorBoundary/
  // themeToggle/useConfirm/pwa/dailyEntry/dashboard/water/grades/inventory
  // above: `products` is deliberately NOT in TRANSLATED_NAMESPACES, so es/tl
  // fall back to these strings until a native-speaker pass adds the
  // namespace. The Active/Inactive status display on BOTH tables (the
  // products table's StatusBadge and the packed-unit table's plain-text
  // cell) goes through the `enums` `statusLabel` helper, not a key here —
  // see ProductsPage.tsx. `unitCode`/`defaultUnit`, product names, and grade
  // names are free-form farm/API DATA, not client copy, and stay raw — never
  // routed through the catalog or an enum helper. `eggsPerUnit` below is the
  // one COPY string that interpolates a raw DATA value (the packed-unit
  // dialog's title).
  products: {
    title: "Products",

    // Imperative messages (the mount-effect catch, and the price parser's
    // thrown errors — caught inside the create/edit submit handlers — see
    // CONTRIBUTING-i18n.md's imperative i18n.t() pattern).
    loadCatalogFailed: "Could not load the catalog. Is the API up?",
    enterPriceAsNumber: "Enter the price as a plain number.",
    noDecimalPlaces: "This currency has no decimal places.",
    atMostDecimals: "At most {{count}} decimal places for this currency.",

    intro:
      "What the farm sells. Each egg product maps to an egg grade — sales "
      + "draw stock from that grade's lots. Deactivating removes a product "
      + "from pickers; history keeps its name.",

    // Page-head button + New/edit product dialogs. `newProductButton` (the
    // page's action button) and `newProductDialogTitle` share English text
    // today but are separate keys, one per UI role — same treatment as
    // dailyEntry:newFlockButton/newFlockDialogTitle.
    newProductButton: "New product",
    newProductDialogTitle: "New product",
    editProductDialogTitle: "Edit product",

    // Product form labels — identical text in both the create and edit
    // dialogs, so one key each covers both (like inventory:defaultCostLabel).
    nameLabel: "Name",
    gradeLabel: "Grade",
    pickGradeOption: "Pick a grade…",
    soldPerLabel: "Sold per",
    // {{code}} is the account's (create dialog) or the row's own snapshot
    // (edit dialog) currency code — free-form DATA, shared verbatim by both.
    defaultPriceLabel: "Default price",
    defaultPriceWithCurrencyLabel: "Default price ({{code}})",
    // Lowercase placeholder text, distinct from common.optional ("Optional")
    // — same case-sensitive-variant treatment as grades:saleableYesBadge.
    priceOptionalPlaceholder: "optional",
    notesLabel: "Notes",
    addProductButton: "Add product",

    // Packed-unit (egg-unit-conversion) dialog. {{unitCode}} is the
    // conversion's free-form unit code (DATA) — this is COPY with a data
    // interpolation, not an enum render.
    eggsPerUnit: "Eggs per {{unitCode}}",
    // Fallback dialog title before a conversion is selected — in practice
    // never visible (the dialog only opens once editingConv is set), kept
    // for source fidelity with the pre-sweep ternary.
    packedUnitDialogTitle: "Packed unit",
    eggsPerUnitFieldLabel: "Eggs per unit",
    // Lowercase checkbox label, distinct from enums:status.Active ("Active")
    // — this is the form field, not a status display.
    activeCheckboxLabel: "active",

    // Products table — separate keys from the form labels above even where
    // the English text coincides (same treatment as water:flockLabel/
    // flockHeader).
    noProductsMessage: "No products yet.",
    nameHeader: "Name",
    gradeHeader: "Grade",
    soldPerHeader: "Sold per",
    defaultPriceHeader: "Default price",
    // Shared by BOTH tables on this screen (products + packed units) — same
    // word, same status-column meaning, on the same page.
    statusHeader: "Status",
    // Row-action links, shared by BOTH tables' edit buttons (same meaning:
    // open that row's edit dialog).
    editButton: "edit",
    deactivateButton: "deactivate",
    activateButton: "activate",

    // Packed units table
    packedUnitsHeading: "Packed units",
    packedUnitsIntro:
      "How many eggs each unit holds when selling (a carton is 12, 18, or "
      + "30 depending on your market — set yours). Changing a unit only "
      + "affects future sales; recorded orders keep the count they were "
      + "sold with.",
    unitHeader: "Unit",
    eggsPerUnitHeader: "Eggs per unit",
    alwaysOneMessage: "always 1",
  },
  // Egg stock summary + drill-down screen — by-grade balances expand into
  // lots, and each lot expands into its movement ledger (Task 18, #182, batch
  // B3 — the last B3 screen). English-only for now, same treatment as
  // nav/numberField/errorBoundary/themeToggle/useConfirm/pwa/dailyEntry/
  // dashboard/water/grades/inventory/products above: `stock` is deliberately
  // NOT in TRANSLATED_NAMESPACES, so es/tl fall back to these strings until a
  // native-speaker pass adds the namespace. The one closed-vocabulary display
  // on this screen — the movement ledger's Type cell — goes through the
  // `enums` `stockMovementLabel` helper, not a key here — see StockPage.tsx.
  // All eight EggMovementType values are identity-labelled in English today,
  // so wiring it changes nothing visible (confirmed in the report). There is
  // no Active/Inactive status display on this screen. Grade/lot names, dates,
  // and quantity/delta values are free-form farm DATA, not client copy, and
  // stay raw — never routed through the catalog or an enum helper.
  stock: {
    title: "Stock",

    // Imperative messages (mount-effect / drill-down catch handlers — see
    // CONTRIBUTING-i18n.md's imperative i18n.t() pattern).
    loadStockFailed: "Could not load stock. Is the API up?",
    loadLotsFailed: "Could not load the grade's lots.",
    loadMovementsFailed: "Could not load the lot's movements.",

    noStockMessage: "No stock yet — record and submit a daily entry.",

    // By-grade stock table
    gradeHeader: "Grade",
    // Shared by BOTH tables on this screen (by-grade balances + the lots
    // drill-down) — same word, same "quantity available" meaning, on the
    // same page, same treatment as products:statusHeader.
    availableHeader: "Available",
    restrictedHeader: "Restricted",
    lotsButton: "lots",
    hideLotsButton: "hide lots",
    // {{available}}/{{grades}} are the client-side reduce totals — plain
    // numeric DATA, not enum-labelled.
    totalAvailableMessage:
      "{{available}} eggs available across {{grades}} grade(s). "
      + "Restricted = under medication withdrawal, blocked from sale.",

    // Lots drill-down (per grade)
    lotsHeading: "Lots",
    noLotsMessage: "No lots for this grade yet.",
    producedOnHeader: "Produced on",
    producedHeader: "Produced",
    historyButton: "history",
    hideHistoryButton: "hide history",

    // Movement ledger drill-down (per lot). Type reads the RAW server value
    // through stockMovementLabel — the full EggMovementType enum is covered
    // (there is no picker on this read-only screen).
    movementLedgerHeading: "Movement ledger",
    movementLedgerIntro:
      "Every change to this lot's available eggs — the running sum always "
      + "equals the balance above.",
    ledgerWhenHeader: "When (UTC)",
    ledgerTypeHeader: "Type",
    ledgerChangeHeader: "Change",
    ledgerReasonHeader: "Reason",
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
  //   1. Dashboard.tsx (`<StatusBadge status={e.status} />`, no label) used to
  //      show a ManagerAdjusted entry RAW; statusLabel (wired in Task 12) now
  //      makes it read "Adjusted" — the text HistoryPage already shows for
  //      that state via its own bespoke `<span className="badge
  //      badge-warn">Adjusted</span>` (HistoryPage.tsx 249-250), NOT a
  //      StatusBadge label.
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
