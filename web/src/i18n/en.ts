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

    // Task 25 (#182, B4) — the rest of AccountPage: the page heading, the
    // role line, and the self-service change-password surface (#165).
    heading: "Account",
    // {{role}} is the signed-in account's own role, passed through
    // enums.ts's roleLabel() by the component (Task 22 convention: role
    // display always goes through roleLabel(), e.g. "ReadOnly" ->
    // "Read-only") — so this stays a plain string placeholder here, not raw
    // union data. <strong> is the only JSX interleaved in this screen's
    // prose, hence the one <Trans> use.
    roleLine: "You are signed in with the <strong>{{role}}</strong> role.",
    changePasswordHeading: "Change password",
    changePasswordHint:
      "Changing your password signs you out everywhere else — this device "
      + "stays signed in.",
    // The trailing " *" is folded into the label text itself, matching how
    // UsersPage's emailFieldLabel/newPasswordFieldLabel already handle a
    // required-field marker — never a standalone "*" key.
    currentPasswordLabel: "Current password *",
    // {{min}} is MIN_LENGTH (AccountPage.tsx) — interpolated, not baked in,
    // so the label and the validation message below can never drift apart.
    newPasswordLabel: "New password (min {{min}} chars) *",
    confirmPasswordLabel: "Confirm new password *",
    changePasswordButton: "Change password",
    passwordMismatchError: "The new passwords don't match.",
    passwordTooShortError: "The new password must be at least {{min}} characters.",
    passwordChangedMessage: "Password changed. Any other devices have been signed out.",
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
  // conversions admin screen (Task 17, #182, batch B3 — second B3 screen).
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
  // B3 — third B3 screen). English-only for now, same treatment as
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
  // Flock roster + bird ledger — create/edit identity fields, deplete/archive/
  // reactivate lifecycle, and mortality/cull/adjustment movements (Task 19,
  // #182, batch B3 — the last B3 screen (B4–B6 still remain in the sweep).
  // English-only for now, same treatment as
  // nav/numberField/errorBoundary/themeToggle/useConfirm/pwa/dailyEntry/
  // dashboard/water/grades/inventory/products/stock above: `flocks` is
  // deliberately NOT in TRANSLATED_NAMESPACES, so es/tl fall back to these
  // strings until a native-speaker pass adds the namespace. Two
  // closed-vocabulary displays go through `enums` helpers rather than a key
  // here — see FlocksPage.tsx:
  //   1. The bird ledger's Type PICKER (Cull/Adjustment — Mortality is
  //      system-generated from daily-entry deaths, so it never appears there)
  //      AND the ledger's Type CELL (the full Mortality/Cull/Adjustment
  //      vocabulary) both use `flockMovementLabel`.
  //   2. The flocks table's Status badge uses `statusLabel` (identity in
  //      English); `status=` itself stays the raw wire value.
  // Both are identity-labelled in English today, so wiring them changes
  // nothing visible (confirmed in the report). Flock `name`/`breed` are
  // free-form farm DATA, not client copy, and stay raw — never routed through
  // the catalog or an enum helper.
  flocks: {
    title: "Flocks",

    // Imperative messages (mount-effect / ledger-load catch handlers — see
    // CONTRIBUTING-i18n.md's imperative i18n.t() pattern).
    loadFlocksFailed: "Could not load flocks. Is the API up?",
    loadMovementsFailed: "Could not load movements.",

    newFlockButton: "New flock",
    intro:
      "Deplete when the birds are gone; archive to hide a flock from pickers and "
      + "the dashboard. History keeps resolving archived flocks' names.",

    // New-flock dialog (F131)
    newFlockDialogTitle: "New flock",
    nameLabel: "Name *",
    breedLabel: "Breed *",
    placedLabel: "Placed",
    // Reused verbatim by the record-movement dialog below (same text, no
    // asterisk in either) — same treatment as inventory:defaultCostLabel/
    // quantityLabelWithUnit reused across that screen's dialogs.
    birdsLabel: "Birds",
    addFlockButton: "Add flock",

    // Edit-flock dialog — seeded from the row.
    editFlockDialogTitle: "Edit flock",
    editNameLabel: "Edit name",
    editBreedLabel: "Edit breed",
    editPlacedLabel: "Edit placement date",
    editCountLabel: "Edit bird count",

    // Show-archived toggle. {{count}} is the client-side archived-flock
    // count — plain numeric DATA, not enum-labelled.
    showArchivedLabel: "show {{count}} archived",

    noFlocksMessage: "No flocks yet.",

    // Flocks table — separate keys from the form labels above even where the
    // English text coincides (same treatment as water:flockLabel/
    // flockHeader).
    nameHeader: "Name",
    breedHeader: "Breed",
    placedHeader: "Placed",
    ageHeader: "Age",
    birdsHeader: "Birds",
    statusHeader: "Status",
    // {{weeks}} is the client-side ageWeeks() computation — plain numeric
    // DATA, not enum-labelled.
    ageWeeksSuffix: "{{weeks}} wk",

    // Row actions — lowercase link-styled buttons, same treatment as
    // sales:edit/grades:editButton. openLedgerButton/closeLedgerButton toggle
    // the bird-ledger panel below, same treatment as stock:lotsButton/
    // hideLotsButton.
    editButton: "edit",
    depleteButton: "deplete",
    archiveButton: "archive",
    reactivateButton: "reactivate",
    openLedgerButton: "birds",
    closeLedgerButton: "close",

    // Deplete/archive confirm dialogs (title / body / confirmLabel). {{name}}
    // is the flock's free-form NAME (DATA), not client copy.
    depleteConfirmTitle: "Deplete \"{{name}}\"?",
    depleteConfirmBody:
      "The flock stops accepting new entries. Backfilling past dates still works.",
    depleteConfirmLabel: "Deplete flock",
    archiveConfirmTitle: "Archive \"{{name}}\"?",
    archiveConfirmBody:
      "It disappears from pickers and the dashboard, and accepts nothing new.",
    archiveConfirmLabel: "Archive flock",

    // Bird ledger panel. {{name}} is the flock's free-form NAME (DATA).
    ledgerHeading: "Bird ledger — {{name}}",
    ledgerIntro: "Mortality rows come from submitted daily entries.",
    ledgerIntroAdminNote:
      " Record culls here; use a negative adjustment to correct a miscount.",
    ledgerIntroWorkerNote: " Recording culls and adjustments needs an admin.",
    recordMovementButton: "Record movement",

    // Record-movement dialog. The Type picker's two options read the
    // ledger's OWN identity label (flockMovementLabel) — see the namespace
    // header comment above.
    recordMovementDialogTitle: "Record bird movement",
    dateLabel: "Date",
    typeLabel: "Type",
    noteLabel: "Note",
    recordButton: "Record",

    noMovementsMessage: "No movements yet — the flock is at its initial count.",

    // Movement ledger table — separate keys from the form labels above even
    // where the English text coincides (same treatment as the flocks table
    // above).
    ledgerDateHeader: "Date",
    ledgerTypeHeader: "Type",
    ledgerBirdsHeader: "Birds",
    ledgerNoteHeader: "Note",
  },
  // Farm settings — admin localization + logo (#123, #149) + the currency
  // lock (§4.6) (Task 21, #182, batch B4). English-only for now, same
  // treatment as nav/numberField/errorBoundary/themeToggle/useConfirm/pwa/
  // dailyEntry/dashboard/water/grades/inventory/products/stock/flocks above:
  // `settings` IS in TRANSLATED_NAMESPACES: es/tl carry machine-drafted
  // translations (#182, pending native review).
  //
  // DATA left raw, never routed through this catalog: the timezone list
  // (Intl.supportedValuesOf), the locale/currency VALUES the admin types, the
  // "en-US" locale-format example (allowlisted in i18n-scan-allowlist.txt),
  // and the curated palettes' lowercase ids (aubergine/forest/slate/
  // terracotta — matched by exact-match CSS selectors and written into
  // data-brand). Their DISPLAY names (paletteAubergine etc. below) ARE copy.
  //
  // Enum wiring: the Unit system and First day of week SELECTs are closed
  // vocabularies, so their OPTION text is rendered through the shared
  // `enums.ts` helpers (unitSystemLabel/weekdayLabel — both pre-built for
  // this screen, identity labels, no visible change) rather than a key here;
  // unitSystemLabel/firstDayOfWeekLabel below are the FIELD labels, not the
  // option text. The four palette ids have no cross-screen enum family (this
  // is their only render site), so their display names are flat keys here
  // instead — see the PALETTE_LABEL_KEYS map in SettingsPage.tsx.
  settings: {
    heading: "Farm settings",
    intro:
      "How this farm names itself, and the locale, timezone and currency it "
      + "records and reads its work in.",
    loadFailedMessage: "Could not load farm settings.",

    // Logo panel
    logoSectionHeading: "Logo",
    logoAlt: "Current farm logo",
    logoLoadingMessage: "Loading the logo…",
    logoLoadFailedMessage: "The logo could not be loaded.",
    logoNoneMessage: "No logo set — the sidebar shows the Cluckwork mark.",
    uploadLogoButton: "Upload a logo",
    replaceLogoButton: "Replace the logo",
    removeLogoButton: "Remove",
    // {{cap}} is formatByteCap()'s already-formatted string (e.g. "2 MB") —
    // client-side arithmetic, never routed through i18n.language.
    logoRulesHint:
      "PNG, JPEG or WebP, up to {{cap}} and 4096 px a side. Animated "
      + "images are not accepted. The image is stored re-written, with "
      + "camera and location metadata removed.",
    // <Trans> key — the only string on this screen interleaving JSX
    // (<strong>square</strong>), see CONTRIBUTING-i18n.md.
    logoSquareHint:
      "Use a <strong>square</strong> image — the logo shows small in the "
      + "sidebar, so a simple, tightly-cropped mark (a symbol or a single "
      + "letter) reads far better there than a wide or detailed picture. A "
      + "transparent background on a light design works best.",
    logoWorkingMessage: "Working…",
    logoUpdatedMessage: "Logo updated.",
    logoRemovedMessage: "Logo removed.",
    // {{actualKb}}/{{limitKb}} are plain numeric DATA (the picked file's size
    // and the server's own configured cap), not enum-labelled.
    logoOversizeMessage: "That image is {{actualKb}} KB. The limit is {{limitKb}} KB.",
    removeLogoConfirmTitle: "Remove the farm logo?",
    removeLogoConfirmBody:
      "The sidebar goes back to the Cluckwork mark. You can upload another "
      + "at any time.",
    removeLogoConfirmLabel: "Remove logo",

    // Localization form
    localizationSectionHeading: "Localization",
    farmNameLabel: "Farm name",
    timezoneLabel: "Timezone",
    timezoneUnknownWarning:
      "This browser does not know that timezone, so dates here would follow "
      + "the device instead of the farm. Pick one from the list.",
    localeLabel: "Locale",
    currencyLabel: "Currency",
    // {{code}} is the farm's currency CODE (DATA, e.g. "USD"), not enum-labelled.
    currencyLockedNote:
      "The currency is fixed at {{code}}: this farm has already recorded "
      + "amounts in it. Recorded money is never re-priced, so changing this "
      + "would leave every stored total meaning something else.",
    unitSystemLabel: "Unit system",
    firstDayOfWeekLabel: "First day of week",
    // Reused for the First-day-of-week "no override" option AND both the
    // date/time format placeholders — same English text, same meaning, in
    // all three spots.
    followLocaleOption: "Follow the locale",
    paletteLegend: "Farm palette",
    paletteHint:
      "The accent colour for everyone on this farm. Each person still "
      + "chooses light or night mode for themselves.",
    // Curated palette DISPLAY names (#149) — the ids themselves stay raw DATA
    // (see the namespace header comment above).
    paletteAubergine: "Aubergine",
    paletteForest: "Forest",
    paletteSlate: "Slate",
    paletteTerracotta: "Terracotta",
    dateFormatLabel: "Date format",
    timeFormatLabel: "Time format",
    savingButton: "Saving…",
    saveButton: "Save settings",
    effectNote:
      "The timezone applies everywhere as soon as it is saved. Locale, unit "
      + "system and the format overrides are recorded against the farm and "
      + "will drive how amounts, dates and measurements are displayed once "
      + "that formatting lands.",
    savedMessage: "Settings saved.",

    // Imperative messages (event handlers — see CONTRIBUTING-i18n.md's
    // imperative i18n.t() pattern).
    versionConflictMessage:
      "Someone else changed these settings while this screen was open. "
      + "Reload and try again.",
    saveReadBackFailedMessage:
      "Saved. This screen could not read the settings back — reload the "
      + "page before saving again.",
    refreshFailedMessage:
      "Saved. The rest of the app could not pick the change up — reload the "
      + "page to be sure it is applied everywhere.",
  },
  // Users admin screen — the user list plus create/edit/password/flock-
  // scoping dialogs (Task 22, #182, batch B4). English-only for now, same
  // treatment as nav/numberField/errorBoundary/themeToggle/useConfirm/pwa/
  // dailyEntry/dashboard/water/grades/inventory/products/stock/flocks/
  // settings above. `users` IS in TRANSLATED_NAMESPACES: es/tl carry
  // machine-drafted translations (#182, pending native review).
  //
  // Role enum wiring: the table's Role cell, the create-form Role picker's
  // option text, and the create-success message all render the closed
  // `role` vocabulary through roleLabel() (enums.ts) rather than a key here
  // — see the `enums` namespace header comment below for the one
  // intentional visible change that wiring makes (ReadOnly -> "Read-only").
  // adminRoleOption below is the one exception: the picker's Admin option
  // carries an extra "(owner)" qualifier that isn't part of the role's
  // identity label, so it wraps roleLabel("Admin") rather than being used
  // as the option text on its own.
  users: {
    heading: "Users",
    newUserButton: "New user", // reused verbatim as the create-dialog title
    roleDescription:
      "Workers record the day's work (optionally narrowed to assigned "
      + "flocks). Managers additionally correct, void, and configure. Sales "
      + "handles customers, orders, and payments. Read-only sees stock, "
      + "history, and reports. Admin (owner) does everything, including "
      + "managing users.",

    // Create-user dialog
    emailFieldLabel: "Email *",
    passwordFieldLabel: "Password (min 12 chars) *",
    nameFieldLabel: "Name", // reused verbatim by the edit-user dialog below
    roleFieldLabel: "Role",
    // {{label}} is roleLabel("Admin") ("Admin", identity) — see the
    // namespace header comment above.
    adminRoleOption: "{{label}} (owner)",
    createUserButton: "Create user",

    // Users table
    emailColumnHeader: "Email",
    nameColumnHeader: "Name",
    roleColumnHeader: "Role",
    editButton: "edit",
    resetPasswordButton: "password",
    flocksButton: "flocks",

    // Flock-access dialog (per-worker scoping). {{email}} is the user's
    // email — DATA, not client copy.
    flockAccessTitle: "Flock access — {{email}}",
    flockAccessHint:
      "No assignments = the worker can record for any flock. The first "
      + "assignment narrows them to the listed flocks only.",
    noAssignmentsMessage: "No assignments — account-wide access.",
    removeAssignmentButton: "remove",
    assignFlockButton: "Assign flock",
    doneButton: "Done",

    // Edit-user dialog. {{email}} is DATA.
    editUserTitle: "Edit user — {{email}}",
    clearNameHint: "Leave blank to clear the name.",

    // Set-password dialog. {{email}} is DATA.
    setPasswordTitle: "Set password — {{email}}",
    passwordDialogHint:
      "You don't need their current password. Setting it signs them out "
      + "of every device — tell them the new password directly.",
    newPasswordFieldLabel: "New password (min 12 chars) *",
    confirmPasswordFieldLabel: "Confirm new password *",
    setPasswordButton: "Set password",

    // Imperative messages (event handlers — see CONTRIBUTING-i18n.md's
    // imperative i18n.t() pattern). {{email}} is DATA; {{role}} is
    // roleLabel(role) — see the namespace header comment above.
    createSuccessMessage: "{{role}} account created for {{email}}.",
    passwordMismatchMessage: "The passwords don't match.",
    passwordSetMessage:
      "Password set for {{email}}. They have been signed out everywhere.",
    updatedMessage: "Updated {{email}}.",
  },
  // Expenses screen — category management (a dialog panel: create/deactivate/
  // reactivate) + record/correct expenses, filtered by month and category
  // (Task 23, #182, batch B4 — the last B4 screen). English-only for now, same
  // treatment as nav/numberField/errorBoundary/themeToggle/useConfirm/pwa/
  // dailyEntry/dashboard/water/grades/inventory/products/stock/flocks/
  // settings/users above. `expenses` IS in TRANSLATED_NAMESPACES: es/tl carry
  // machine-drafted translations (#182, pending native review).
  //
  // PLAN CORRECTION (verified by controller): this screen has NO
  // payment-method/category enum. `ExpenseCategory` rows are free-form,
  // admin-created records (createExpenseCategory), not a closed API
  // vocabulary — so category NAMES stay raw DATA everywhere they render (the
  // filter option, the add/edit pickers, the category-list rows, the
  // deactivated-suffix rows) and are never routed through an enums.ts helper.
  // Expense descriptions, notes, dates, amounts, and flock names are the same
  // kind of free-form farm DATA and stay raw too. Money stays on the existing
  // farm-locale `formatMoney` — never `i18n.language` (formattingIndependence
  // guard).
  //
  // Money-decimal validation copy (enterValidAmount/noDecimalPlaces/
  // atMostDecimals/enterAmountGreaterThanZero) duplicates sales/products'
  // near-identical strings but is kept as its OWN local key set here per the
  // task brief — a cross-screen consolidation is a tracked native-pass
  // deferral, not this task's job.
  expenses: {
    title: "Expenses",

    // Imperative messages (event handlers — see CONTRIBUTING-i18n.md's
    // imperative i18n.t() pattern).
    expenseRecordedMessage: "Expense recorded.",
    expenseCorrectedMessage: "Expense corrected.",
    // 409 rebind (onSaveEdit): someone else corrected this expense while the
    // dialog was open — the panel is rebound to the server's latest values.
    conflictRebindMessage:
      "This expense was changed by someone else — the form now shows the "
      + "latest values; re-apply your correction.",
    categoryCreatedMessage: "Category created.",
    // {{name}} is the category's free-form NAME (DATA), not client copy.
    categoryDeactivatedMessage: "Category \"{{name}}\" deactivated.",
    categoryReactivatedMessage: "Category \"{{name}}\" reactivated.",

    // Amount-parsing validation (toMinorUnits, thrown from the add/edit
    // submit handlers — imperative i18n.t() pattern, same shape as
    // products:enterPriceAsNumber/noDecimalPlaces/atMostDecimals).
    enterValidAmount: "Enter a valid amount.",
    noDecimalPlaces: "This currency has no decimal places.",
    atMostDecimals: "At most {{count}} decimal places for this currency.",
    enterAmountGreaterThanZero: "Enter an amount greater than zero.",

    // Filters
    monthLabel: "Month",
    // Shared by the filter select, the add-form select, and the edit-form
    // select — all three already carry this identical label in source.
    categoryLabel: "Category",
    allCategoriesOption: "All categories",
    hideCategoriesButton: "hide categories",
    manageCategoriesButton: "manage categories",
    // {{amount}} is formatMoney's already-formatted total — farm-locale DATA,
    // never routed through i18n.language.
    monthTotalLabel: "Month total: {{amount}}",

    // Category-management panel
    categoriesHeading: "Expense categories",
    newCategoryButton: "New category",
    newCategoryDialogTitle: "New expense category",
    categoryNameLabel: "Category name",
    addCategoryButton: "Add category",
    // Appended to a deactivated category's free-form NAME (DATA) — shared by
    // the filter option, the category-list row, and the edit-form select.
    deactivatedSuffix: " (deactivated)",
    deactivateButton: "deactivate",
    reactivateButton: "reactivate",
    noCategoriesMessage: "No categories yet — add one above.",

    // Record-expense form
    recordExpenseHeading: "Record an expense",
    dateLabel: "Date", // shared by the add and edit forms
    pickOption: "— pick —",
    descriptionLabel: "Description", // shared by the add and edit forms
    // {{code}} is the loaded/snapshot currency CODE (DATA) — shared by the
    // add form (currency.code) and the edit form (editing.currencyCode).
    amountLabel: "Amount ({{code}})",
    flockOptionalLabel: "Flock (optional)", // shared by the add and edit forms
    noneOption: "— none —",
    noteOptionalLabel: "Note (optional)", // shared by the add and edit forms
    recordExpenseButton: "Record expense",
    addCategoryFirstMessage: "Add a category first — every expense needs one.",

    // Correct-expense dialog. {{date}}/{{description}} are the expense's own
    // free-form DATA, not client copy.
    correctExpenseDialogTitle: "Correct expense",
    correctExpenseDialogTitleWithExpense: "Correct — {{date}}, {{description}}",
    saveCorrectionButton: "Save correction",

    // Expenses table
    noExpensesMessage: "No expenses for this month.",
    dateHeader: "Date",
    categoryHeader: "Category",
    descriptionHeader: "Description",
    amountHeader: "Amount",
    flockHeader: "Flock",
    noteHeader: "Note",
    correctButton: "correct",
    loadMoreButton: "load more",
  },
  // Customers screen — the customer book: list + create dialog, with an
  // admin-only outstanding-balance column (Task 24, #182, batch B4).
  // English-only for now, same treatment as nav/numberField/errorBoundary/
  // themeToggle/useConfirm/pwa/dailyEntry/dashboard/water/grades/inventory/
  // products/stock/flocks/settings/users/expenses above. `customers` IS in
  // TRANSLATED_NAMESPACES: es/tl carry machine-drafted translations
  // (#182, pending native review).
  //
  // No status/enum on this screen — Customer has no closed-vocabulary field,
  // so nothing here routes through enums.ts. Customer name/phone/email/
  // address/note are free-form farm DATA (createCustomer's own fields) and
  // stay raw everywhere they render, including the "—" placeholder for a
  // blank optional field (shared convention across the sweep — see
  // ExpensesPage/UsersPage/FlocksPage, which leave the em dash un-keyed too).
  // Outstanding balances stay on the existing farm-locale `formatMoney` —
  // never `i18n.language` (formattingIndependence guard).
  customers: {
    title: "Customers",
    newCustomerButton: "New customer", // reused verbatim as the create-dialog title

    // Create-customer dialog
    nameFieldLabel: "Name *",
    phoneFieldLabel: "Phone *",
    emailFieldLabel: "Email",
    addressFieldLabel: "Address",
    noteFieldLabel: "Note",
    addCustomerButton: "Add customer",

    // Imperative messages (mount-effect .catch callbacks — see
    // CONTRIBUTING-i18n.md's imperative i18n.t() pattern).
    loadCustomersErrorMessage: "Could not load customers.",
    loadBalancesErrorMessage: "Could not load customer balances.",

    // Customers table
    noCustomersMessage: "No customers yet.",
    nameHeader: "Name",
    phoneHeader: "Phone",
    emailHeader: "Email",
    addressHeader: "Address",
    noteHeader: "Note",
    outstandingHeader: "Outstanding",
  },
  // Daily-entry history — browse recorded entries with flock/date filters,
  // plus the admin-only adjust/void corrections (Task 27, #182, batch B5 —
  // the last B4-onward records/export screen). English-only for now, same
  // treatment as nav/numberField/errorBoundary/themeToggle/useConfirm/pwa/
  // dailyEntry/dashboard/water/grades/inventory/products/stock/flocks/
  // settings/users/expenses/customers above: `history` is deliberately NOT in
  // TRANSLATED_NAMESPACES, so es/tl fall back to these strings until a
  // native-speaker pass adds the namespace.
  //
  // Entry-status pills (statusCell): DELIBERATELY NOT routed through the
  // shared `enums` status family, even though enums.status already carries
  // Draft/Submitted/Locked/ManagerAdjusted/Voided (Task 4/12) — this screen's
  // bespoke pills predate that helper and keep their OWN display text here
  // (statusVoided/statusAdjusted/statusLocked/statusSubmitted/statusDraft),
  // per the task brief. Text is identical to enums' labels (identity, plus
  // ManagerAdjusted -> "Adjusted"), so this is a naming/ownership choice, not
  // a visible difference — see the `enums` namespace header comment, which
  // already points AT this screen's own "Adjusted" pill as the precedent
  // Dashboard's retrofit matched. lockedAt interpolates the raw lockedAtUtc
  // timestamp (DATA) into the tooltip text (COPY).
  //
  // The nothingToAdjustMessage 409-rebind message interpolates
  // fresh.status.toLowerCase() — lowercasing a raw wire enum value is
  // locale-fragile (only ever reads correctly in English); tracked as a
  // native-pass deferral (#182), not solved by this task (see the code
  // comment at the call site, HistoryPage.tsx rebindAfterConflict).
  //
  // DATA left raw, never routed through this catalog: entry dates
  // (e.date/adjusting.date), flock/grade names (flockName/gradeName, with
  // their id.slice(0,8) fallback), the "—" placeholder for an entry with no
  // graded lines (same convention as customers/expenses/users/flocks — see
  // the `customers` namespace header comment above), free-form void/adjust
  // reasons (voidReason/adjustReason, shown via the `title` attribute or
  // interpolated verbatim into previouslyAdjusted), and every numeric count
  // (totalEggs/crackedEggs/etc., adjustedFrom's snapshot counts).
  history: {
    // Titles. `loadingTitle` is the shorter heading the early-return
    // load-error state uses before the real heading below — a pre-existing
    // inconsistency, preserved verbatim rather than "fixed" as a drive-by
    // (same treatment as grades:loadingTitle/title).
    loadingTitle: "History",
    title: "Daily entry history",

    intro:
      "Submitted and locked entries can be adjusted or voided here — stock "
      + "and the bird ledger follow automatically; eggs already sold never "
      + "move. A reason is always required.",

    // Imperative messages (event handlers / promise callbacks — see
    // CONTRIBUTING-i18n.md's imperative i18n.t() pattern). Several of these
    // are near-duplicate concurrent-conflict copy at different call sites
    // (errText's own 409 branch vs. onVoid's inline 409 branch, and the two
    // "could not reload" variants) — kept as separate, text-preserving keys
    // rather than consolidated, matching the sweep's convention of not
    // drive-by-fixing pre-existing copy drift.
    concurrentConflictMessage:
      "This entry was just changed elsewhere — the list has been reloaded; retry.",
    loadFlocksGradesFailed: "Could not load flocks/grades.",
    loadEntriesFailed: "Could not load entries.",
    conflictRebindMessage:
      "This entry was changed by someone else — the form shows the latest "
      + "values; re-apply your correction.",
    // {{status}} is the RAW wire status, lowercased — see the locale-fragile
    // note in the namespace header comment above.
    nothingToAdjustMessage: "This entry is now {{status}} — nothing left to adjust.",
    conflictReloadFailedMessage:
      "This entry was changed by someone else and the list could not be "
      + "reloaded — reload the page before retrying.",
    exceedsSellableMessage:
      "Graded quantities cannot exceed total eggs minus cracked/dirty/discarded.",
    entryAdjustedMessage: "Entry adjusted — stock and bird ledger updated to match.",
    adjustReloadFailedMessage: "The adjustment saved, but the list failed to reload — refresh the page.",
    // askReason dialog (title / body / confirmLabel). {{date}}/{{flock}} are
    // the entry's own free-form DATA (date string, resolved flock name).
    voidConfirmTitle: "Void the {{date}} entry for {{flock}}?",
    voidConfirmBody:
      "Its egg lots empty and its deaths are reversed. The entry is kept as "
      + "Voided. Refused if any of its eggs were already sold.",
    voidConfirmLabel: "Void entry",
    entryVoidedMessage: "Entry voided — its egg lots were emptied and its deaths reversed.",
    voidReloadFailedMessage: "The void saved, but the list failed to reload — refresh the page.",
    voidConflictMessage: "This entry was changed by someone else — the list has been reloaded; retry.",
    voidConflictReloadFailedMessage:
      "This entry was changed by someone else and the list could not be "
      + "reloaded — reload the page.",
    loadMoreFailedMessage: "Could not load more.",

    // Filters
    flockLabel: "Flock",
    allFlocksOption: "All flocks",
    fromLabel: "From",
    toLabel: "To",

    // Adjust dialog — title (two shapes: an entry bound vs. the fallback
    // before one is), the "previously adjusted" recap, and the form.
    adjustDialogTitle: "Adjust entry",
    adjustDialogTitleWithEntry: "Adjust — {{date}}, {{flock}}",
    // {{total}}/{{mortality}} are the prior snapshot's numeric DATA;
    // {{reason}} is the free-form adjustReason DATA.
    previouslyAdjusted:
      "Previously adjusted (was total {{total}}, mortality {{mortality}} — \"{{reason}}\").",
    totalEggsLabel: "Total eggs",
    crackedLabel: "Cracked",
    dirtyLabel: "Dirty",
    discardedLabel: "Discarded",
    deathsLabel: "Deaths",
    // Appended to a grade line's free-form NAME (DATA) for a deactivated
    // grade still on the entry — distinct wording from
    // dailyEntry:deactivatedGradeSuffix (" (deactivated)"), preserved
    // verbatim rather than reconciled (not this task's job).
    inactiveGradeSuffix: " (inactive)",
    reasonLabel: "Reason *",
    saveAdjustmentButton: "Save adjustment",

    noEntriesMatch: "No entries match — record one on the Daily entry page.",

    // Entries table
    dateHeader: "Date",
    flockHeader: "Flock",
    statusHeader: "Status",
    totalHeader: "Total",
    lossesHeader: "Losses (cr/di/ds)",
    mortalityHeader: "Mortality",
    gradedHeader: "Graded",
    // Lowercase link-styled row actions, same treatment as
    // sales:edit/water:correctButton/grades:editButton.
    editButton: "edit",
    adjustButton: "adjust",
    voidButton: "void",
    loadMoreButton: "load more",

    // Entry-status pills (statusCell) — see the namespace header comment
    // above for why this is a separate vocabulary from enums:status.
    statusVoided: "Voided",
    statusAdjusted: "Adjusted",
    statusLocked: "Locked",
    // {{time}} is the raw lockedAtUtc timestamp (DATA).
    lockedAt: "Locked {{time}}",
    statusSubmitted: "Submitted",
    statusDraft: "Draft",
  },
  // Task 28 (#182, B5) — ReportsPage. English-only (not in
  // TRANSLATED_NAMESPACES), same as `history`/`nav`. Production renders for
  // everyone; the Money section (sales/expenses/profit) is admin-gated —
  // isAdmin is checked by the component, not this catalog.
  //
  // DATA left raw, never routed through this catalog: every date
  // (d.date/from/to), every numeric count/total (totalEggs, cracked/dirty/
  // discarded, sellable, deaths, henDays, henDayPct and their period
  // equivalents), the "—" em-dash fallback for a null henDayPct (same
  // convention as customers/expenses/users/flocks/history's raw "—"), the
  // grade-totals and expense-category lists (built from free-form
  // name/quantity DATA via template-literal `.join(", ")`, matching
  // HistoryPage's gradeName-list precedent), and every money string
  // (formatMoney is farm-locale-driven, never keyed off i18n.language —
  // interpolated into the templates below as pre-formatted DATA, same
  // pattern as sales:orderTotal).
  reports: {
    title: "Reports",
    fromLabel: "From",
    toLabel: "To",

    productionHeading: "Production",
    dateHeader: "Date",
    eggsHeader: "Eggs",
    lossesHeader: "Losses (cr/di/ds)",
    sellableHeader: "Sellable",
    deathsHeader: "Deaths",
    henDaysHeader: "Hen-days",
    henDayPctHeader: "Hen-day %",
    periodRowLabel: "Period",
    gradeTotalsLabel: "By grade:",

    moneyHeading: "Money",
    salesRowLabel: "Sales",
    // {{count}} is confirmedCount; {{revenue}}/{{paid}}/{{outstanding}} are
    // pre-formatted formatMoney() strings (DATA).
    salesSummary:
      "{{count}} confirmed order(s) — revenue {{revenue}}, paid {{paid}}, outstanding {{outstanding}}",
    // {{count}} is voidedCount — a second, independent {{count}} interpolation
    // on the same screen, appended only when voidedCount > 0.
    salesVoidedSuffix: " ({{count}} voided)",
    expensesRowLabel: "Expenses",
    expensesNone: "none recorded",
    // {{total}} is expenses.grandTotalMinorUnits, pre-formatted (DATA).
    expensesTotalSuffix: " — total {{total}}",
    profitRowLabel: "Profit (basic)",
    // <Trans> (not t()) — the only JSX interleaved in this screen's prose is
    // the <strong> around the profit figure, same treatment as
    // account:roleLine. {{revenue}}/{{expenses}}/{{profit}} are pre-formatted
    // formatMoney() strings (DATA); the "−" is U+2212 MINUS SIGN (matching
    // the pre-sweep source), not a hyphen.
    profitLine: "revenue {{revenue}} − expenses {{expenses}} = <strong>{{profit}}</strong>",
    profitFootnote:
      "\"Basic\" profit is confirmed revenue minus recorded expenses — no "
      + "cost-of-goods or inventory valuation.",
  },
  // Task 29 (#182, B5) — AuditPage. English-only (not in
  // TRANSLATED_NAMESPACES), same as `history`/`nav`/`reports`. The #93
  // read-only audit trail (admin). The action/entity table cells and the
  // action filter's option text route through enums:auditAction.*/
  // enums:entityType.* (translated — see the `enums` header comment below),
  // NOT this namespace. DATA left raw, never routed through this catalog:
  // every timestamp (e.occurredAtUtc), actorEmail, entityId, and the "—"
  // em-dash fallback for a null reason (same convention as
  // customers/expenses/users/flocks/history's raw "—").
  audit: {
    heading: "Audit log",
    intro:
      "Every corrective, destructive, or configuration change — who did it, "
      + "when, and why. Rows are written with the change itself and never "
      + "edited.",
    actionFilterLabel: "Action",
    allActionsOption: "All actions",
    whenHeader: "When (UTC)",
    whoHeader: "Who",
    actionHeader: "Action",
    entityHeader: "Entity",
    reasonHeader: "Reason",
    emptyMessage: "No audit events yet.",
    loadMoreButton: "load more",
  },
  // Task 30 (B5, #182) — ExportPage: the manual-backup screen (#95,
  // admin-only). English-only (not in TRANSLATED_NAMESPACES), same as
  // `audit`/`history`. CSV column headers and file contents are generated
  // SERVER-side (Cluckwork.Api/Endpoints/Export/CsvExport.cs) and are NOT
  // client strings — out of scope here. Only the visible page copy below
  // (headings, buttons, and the dataset picker's own labels) is externalized;
  // the download filenames (`cluckwork-${d}.csv`, "cluckwork-backup.zip")
  // stay raw, unkeyed — they're functional identifiers, not display copy, and
  // changing them would be a download-mechanics change (out of scope).
  export: {
    heading: "Export",
    intro:
      "Download your account's data as CSV files — a manual backup you can "
      + "keep anywhere. Money values are exported in minor units (cents) "
      + "with their currency, exactly as stored.",

    fullBackupHeading: "Full backup",
    fullBackupButton: "Download full backup (zip)",
    fullBackupHint: "One zip with every dataset below plus a manifest of row counts.",
    // Shared between the full-backup button and every dataset button (each
    // uses its own `busy === <key>` check) — one in-flight-download label,
    // not a per-button duplicate.
    preparingButton: "Preparing…",

    singleDatasetsHeading: "Single datasets",

    // Dataset picker labels — one flat "dataset.<slug>" key per
    // EXPORT_DATASETS member (../api/cluckwork), text IDENTICAL to the raw
    // wire slug (same "value equals the enum member" convention as
    // sales:unit*/method*, en.ts ~L252). Keeps the button's accessible name
    // byte-identical to the pre-sweep raw `{d}` render while routing display
    // through the catalog, as the task brief requires ("a dataset NAME
    // displayed as a picker label is page copy"). A future native-translation
    // pass can replace these with humanized/localized text without touching
    // ExportPage.tsx.
    "dataset.flocks": "flocks",
    "dataset.bird-movements": "bird-movements",
    "dataset.daily-entries": "daily-entries",
    "dataset.daily-entry-grades": "daily-entry-grades",
    "dataset.egg-grades": "egg-grades",
    "dataset.egg-lots": "egg-lots",
    "dataset.customers": "customers",
    "dataset.sales-orders": "sales-orders",
    "dataset.sales-order-items": "sales-order-items",
    "dataset.sales-order-allocations": "sales-order-allocations",
    "dataset.payments": "payments",
    "dataset.inventory-items": "inventory-items",
    "dataset.inventory-lots": "inventory-lots",
    "dataset.inventory-movements": "inventory-movements",
    "dataset.feed-usages": "feed-usages",
    "dataset.water-usages": "water-usages",
    "dataset.expense-categories": "expense-categories",
    "dataset.expenses": "expenses",
    "dataset.egg-inventory-movements": "egg-inventory-movements",
    "dataset.audit-events": "audit-events",
  },
  // Closed-vocabulary labels (#182, Task 4). Consumed ONLY through the typed
  // helpers in enums.ts — never a raw t("enums:status." + value). Keys are FLAT
  // "family.RawValue" strings (keySeparator:false, see index.ts): the suffix is
  // the EXACT wire value the API sends, so the helper's Record can map value →
  // key mechanically and the key is self-documenting. `enums` IS in
  // TRANSLATED_NAMESPACES: es/tl carry machine-drafted enum translations
  // (#182, pending native review).
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

    // audit action (AuditPage filter/table) — AuditEvent.action, a server
    // "Entity.Verb" capture-point code (#182, Task 29). Flat
    // "auditAction.Entity.Verb" keys: keySeparator:false treats the whole
    // dotted code as ONE literal key segment, so e.g. "DailyEntry.Adjust"
    // becomes the key "auditAction.DailyEntry.Adjust", not a nested path.
    "auditAction.DailyEntry.Adjust": "Daily entry adjusted",
    "auditAction.DailyEntry.Void": "Daily entry voided",
    "auditAction.SalesOrder.Void": "Sales order voided",
    "auditAction.Payment.Void": "Payment voided",
    "auditAction.Expense.Adjust": "Expense adjusted",
    "auditAction.ExpenseCategory.Update": "Expense category updated",
    "auditAction.InventoryItem.Adjust": "Inventory item adjusted",
    "auditAction.WaterUsage.Correct": "Water usage corrected",
    "auditAction.Flock.BirdMovement": "Bird movement recorded",
    "auditAction.Flock.Update": "Flock updated",
    "auditAction.Flock.Deplete": "Flock depleted",
    "auditAction.Flock.Archive": "Flock archived",
    "auditAction.Flock.Reactivate": "Flock reactivated",
    "auditAction.EggGrade.Update": "Egg grade updated",
    "auditAction.EggGrade.Activate": "Egg grade activated",
    "auditAction.EggGrade.Deactivate": "Egg grade deactivated",
    "auditAction.User.Create": "User created",
    "auditAction.User.Update": "User updated",
    "auditAction.User.PasswordSet": "Password set",
    "auditAction.User.PasswordChanged": "Password changed",
    "auditAction.User.FlockAssign": "Flock assigned to user",
    "auditAction.User.FlockUnassign": "Flock unassigned from user",
    "auditAction.Account.Export": "Data exported",
    "auditAction.Product.Create": "Product created",
    "auditAction.Product.Update": "Product updated",
    "auditAction.Product.Activate": "Product activated",
    "auditAction.Product.Deactivate": "Product deactivated",
    "auditAction.EggUnitConversion.Update": "Egg unit conversion updated",

    // entity type (AuditPage table entity cell) — AuditEvent.entityType.
    "entityType.Account": "Account",
    "entityType.DailyEntry": "Daily entry",
    "entityType.EggGrade": "Egg grade",
    "entityType.EggUnitConversion": "Egg unit conversion",
    "entityType.Expense": "Expense",
    "entityType.ExpenseCategory": "Expense category",
    "entityType.Flock": "Flock",
    "entityType.InventoryItem": "Inventory item",
    "entityType.Payment": "Payment",
    "entityType.Product": "Product",
    "entityType.SalesOrder": "Sales order",
    "entityType.User": "User",
    "entityType.WaterUsage": "Water usage",
  },
} as const;

export type Resources = typeof en;
