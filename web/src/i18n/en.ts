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
    working: "Working…",
    // The Help page's one line on busy buttons (#236) — kept beside `working`,
    // the announcement it explains.
    workingHint:
      "A spinning button means the save is still working — pressing it again will not record the same thing twice.",
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
    credentialsSuperseded: "Your credentials changed. Please sign in again.",
    accountDisabled: "Your account has been disabled.",
    tooManyAttempts:
      "Too many sign-in attempts. Please wait a few minutes and try again.",
    apiDown: "Could not sign in. Is the API running?",
    // #309 — the request body exceeded the server's byte cap (413), which in
    // practice means an implausibly long email/password.
    credentialsTooLong: "That's too long — check your email and password.",
    // #283 follow-up — shown on the login form when a FAILED sign-in reports
    // that the default account still has no Owner. Base data ships in the
    // migrations but no credential ever does, so a fresh install has nobody for
    // the operator to sign in as. Deliberately names no command — see the
    // comment in Login.tsx.
    // Says ADMINISTRATOR, never "no accounts" (PR #363 review): the condition
    // is specifically that the DEFAULT ACCOUNT has no Owner, and a non-Owner
    // user can exist without one and sign in perfectly well — so the broader
    // claim would be false in a reachable state.
    noAdminYet:
      "No administrator yet. This farm hasn't finished first-time setup, so "
      + "there's no administrator account to sign in with.",
    // No command here on purpose — see the comment in Login.tsx. Whoever set the
    // server up runs a one-time setup step; the README carries the exact steps.
    noAdminYetHint:
      "Ask whoever set up this server to create the first administrator. The "
      + "setup steps are in the project README.",
    // #283 — the first-run "set your password" screen (SetPasswordPage),
    // shown instead of the app shell whenever the signed-in user's token
    // carries must_change_password. Reuses /auth/change-password: the
    // operator already knows the generated temporary password.
    setPasswordHeading: "Set your password",
    setPasswordHint:
      "This is your first sign-in. Set a new password to continue — the "
      + "temporary one won't work again after this.",
    temporaryPasswordLabel: "Temporary password",
    setPasswordNewLabel: "New password (min {{min}} chars)",
    setPasswordConfirmLabel: "Confirm new password",
    setPasswordButton: "Set password",
    setPasswordSubmitting: "Setting password…",
    setPasswordSignOut: "Sign out",
    setPasswordMismatchError: "The new passwords don't match.",
    setPasswordTooShortError: "The new password must be at least {{min}} characters.",
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
  // batch (B1). `nav` is in TRANSLATED_NAMESPACES — es/tl are machine-drafted
  // (#182), pending native review. `navGroups()`/`tabEntries()`
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
  // new copy of their own. Both namespaces are in TRANSLATED_NAMESPACES — es/tl
  // are machine-drafted (#182), pending native review, same treatment as
  // `nav` above.
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
  // Both namespaces are in TRANSLATED_NAMESPACES, same treatment as
  // `nav`/`numberField`/`errorBoundary` above: es/tl are machine-drafted
  // (#182), pending native review.
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

    // Status-filter options. The Draft/Confirmed/Cancelled/Voided labels are
    // rendered from the shared `enums:status` family via statusLabel() (#182) —
    // no local duplicate here. `allOption` is filter-only chrome ("no status
    // filter"), which has no enums equivalent, so it stays in this namespace.
    allOption: "All",

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
  // Daily entry capture screen (Task 11, #182, batch B2). `dailyEntry` is in
  // TRANSLATED_NAMESPACES, same treatment as nav/numberField/errorBoundary/
  // themeToggle/useConfirm/pwa above: es/tl are machine-drafted (#182),
  // pending native review. The one closed-vocabulary display on this screen
  // (the entry-locked banner's status word) goes through the `enums`
  // `statusLabel` helper, not a key here — see DailyEntryPage.tsx.
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
  // sales (Task 12, #182, batch B2). `dashboard` is in TRANSLATED_NAMESPACES,
  // same treatment as nav/numberField/errorBoundary/themeToggle/useConfirm/
  // pwa/dailyEntry above: es/tl are machine-drafted (#182), pending native
  // review. The two status pills on this screen (today's per-flock entry
  // status, and each recent order's status) go through the `enums`
  // `statusLabel` helper, not a key here — see Dashboard.tsx. Wiring the
  // per-flock entry pill is an INTENTIONAL harmonization, not
  // text-preserving: a ManagerAdjusted entry
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
  // `water` is in TRANSLATED_NAMESPACES, same treatment as
  // nav/numberField/errorBoundary/themeToggle/useConfirm/pwa/dailyEntry/
  // dashboard above: es/tl are machine-drafted (#182), pending native
  // review. The two
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
  // screen). `grades` is in TRANSLATED_NAMESPACES, same treatment as
  // nav/numberField/errorBoundary/themeToggle/useConfirm/pwa/dailyEntry/
  // dashboard/water above: es/tl are machine-drafted (#182), pending native
  // review. The one closed-vocabulary display on this screen — the Type
  // picker + table cell —
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
  // #182, batch B3 — the biggest B3 screen). `inventory` is in
  // TRANSLATED_NAMESPACES, same treatment as nav/numberField/errorBoundary/
  // themeToggle/useConfirm/pwa/dailyEntry/dashboard/water/grades above: es/tl
  // are machine-drafted (#182), pending native review. Two closed-vocabulary
  // displays go through `enums` helpers rather than a key here — see
  // InventoryPage.tsx:
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
  // `products` is in TRANSLATED_NAMESPACES, same treatment as
  // nav/numberField/errorBoundary/themeToggle/useConfirm/pwa/dailyEntry/
  // dashboard/water/grades/inventory above: es/tl are machine-drafted (#182),
  // pending native review. The Active/Inactive status display on BOTH
  // tables (the products table's StatusBadge and the packed-unit table's plain-text
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
  // B3 — third B3 screen). `stock` is in TRANSLATED_NAMESPACES, same
  // treatment as nav/numberField/errorBoundary/themeToggle/useConfirm/pwa/
  // dailyEntry/dashboard/water/grades/inventory/products above: es/tl are
  // machine-drafted (#182), pending native review. The one closed-vocabulary
  // display on this screen — the movement ledger's Type cell — goes through the
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
  // `flocks` is in TRANSLATED_NAMESPACES, same treatment as
  // nav/numberField/errorBoundary/themeToggle/useConfirm/pwa/dailyEntry/
  // dashboard/water/grades/inventory/products/stock above: es/tl are
  // machine-drafted (#182), pending native review. Two closed-vocabulary
  // displays go through `enums` helpers rather than a key here — see
  // FlocksPage.tsx:
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
  // lock (§4.6) (Task 21, #182, batch B4). `settings` is in
  // TRANSLATED_NAMESPACES, same treatment as nav/numberField/errorBoundary/
  // themeToggle/useConfirm/pwa/dailyEntry/dashboard/water/grades/inventory/
  // products/stock/flocks above: es/tl are machine-drafted (#182), pending
  // native review.
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
  // scoping dialogs (Task 22, #182, batch B4). `users` is in
  // TRANSLATED_NAMESPACES, same treatment as nav/numberField/errorBoundary/
  // themeToggle/useConfirm/pwa/dailyEntry/dashboard/water/grades/inventory/
  // products/stock/flocks/settings above: es/tl are machine-drafted (#182),
  // pending native review.
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

    // #308 — step-up re-confirmation, shown only for the two sensitive cases
    // (creating another Owner; resetting an existing Owner's password).
    // Shared field label; distinct hints explain WHY it appeared for each.
    stepUpFieldLabel: "Your current password *",
    stepUpCreateHint: "Creating another Owner needs your current password again.",
    stepUpResetHint: "Resetting an Owner's password needs your current password again.",

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
  // (Task 23, #182, batch B4 — the last B4 screen). `expenses` is in
  // TRANSLATED_NAMESPACES, same treatment as nav/numberField/errorBoundary/
  // themeToggle/useConfirm/pwa/dailyEntry/dashboard/water/grades/inventory/
  // products/stock/flocks/settings/users above: es/tl are machine-drafted
  // (#182), pending native review.
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
  // `customers` is in TRANSLATED_NAMESPACES, same treatment as
  // nav/numberField/errorBoundary/themeToggle/useConfirm/pwa/dailyEntry/
  // dashboard/water/grades/inventory/products/stock/flocks/settings/users/
  // expenses above: es/tl are machine-drafted (#182), pending native
  // review.
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
  // the last B4-onward records/export screen). `history` is in
  // TRANSLATED_NAMESPACES, same treatment as nav/numberField/errorBoundary/
  // themeToggle/useConfirm/pwa/dailyEntry/dashboard/water/grades/inventory/
  // products/stock/flocks/settings/users/expenses/customers above: es/tl are
  // machine-drafted (#182), pending native review.
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
    // #394 — an adjustment has no draft state: grading must reconcile
    // EXACTLY to the sellable count (short or over both trigger this),
    // the same rule Daily Entry's submit uses.
    gradesMustReconcileMessage:
      "Graded quantities must equal total eggs minus cracked, dirty, and discarded eggs.",
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
    // The dialog's two steps, its count labels and its reconciliation chip come
    // from the `dailyEntry` namespace: the correction form IS that screen's
    // form, and a second near-duplicate set here is how the two drifted (this
    // namespace used to say "Deaths" where the capture screen said
    // "Mortality"). Only what is genuinely this dialog's own stays below.
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
  // Task 28 (#182, B5) — ReportsPage. `reports` is in TRANSLATED_NAMESPACES,
  // same as `history`/`nav`: es/tl are machine-drafted (#182), pending
  // native review. Production renders for everyone; the Money section
  // (sales/expenses/profit) is admin-gated — isAdmin is checked by the
  // component, not this catalog.
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
  // Task 29 (#182, B5) — AuditPage. `audit` is in TRANSLATED_NAMESPACES, same
  // as `history`/`nav`/`reports`: es/tl are machine-drafted (#182), pending
  // native review. The #93 read-only audit trail (admin). The action/entity
  // table cells and the action filter's option text route through
  // enums:auditAction.*/enums:entityType.* (also translated — see the
  // `enums` header comment below), NOT this namespace. DATA left raw, never
  // routed through this catalog:
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
  // admin-only). `export` is in TRANSLATED_NAMESPACES, same as
  // `audit`/`history`: es/tl are machine-drafted (#182), pending native
  // review. CSV column headers and file contents are generated SERVER-side
  // (Cluckwork.Api/Endpoints/Export/CsvExport.cs) and are NOT
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
  // Payment method and sale unit are intentionally absent — they already live
  // in the `sales` namespace (sales:method*/unit*, which carries its own
  // es/tl). `enums` is in TRANSLATED_NAMESPACES too (#182, pending native
  // review), so duplicating them here is no longer needed to avoid an
  // English-only regression — it would just be a redundant second copy of
  // the same vocabulary.
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
    "auditAction.User.BreakGlassReset": "Break-glass reset",
    "auditAction.User.FlockAssign": "Flock assigned to user",
    "auditAction.User.FlockUnassign": "Flock unassigned from user",
    "auditAction.Account.Export": "Data exported",
    "auditAction.Account.SetLogo": "Farm logo set",
    "auditAction.Account.RemoveLogo": "Farm logo removed",
    "auditAction.Account.UpdateSettings": "Farm settings updated",
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
    "entityType.FarmLogo": "Farm logo",
    "entityType.Flock": "Flock",
    "entityType.InventoryItem": "Inventory item",
    "entityType.Payment": "Payment",
    "entityType.Product": "Product",
    "entityType.SalesOrder": "Sales order",
    "entityType.User": "User",
    "entityType.WaterUsage": "Water usage",
  },

  // Task 32 (B6a, #182): HelpPage prose, getting-around through mistakes
  // (INCLUDING the "Fixing mistakes" table). The glossary table (h3
  // id="glossary") is deliberately NOT here yet — Task 33 (B6b) owns it; see
  // HelpPage.tsx's scope comment. <Trans> keys use NAMED tags (<strong>,
  // <em>) matching the components map at each call site, never numbered
  // <0>/<1>. toc* keys are the contents-rail labels — the rail's `id`s stay
  // hardcoded in HelpPage.tsx (they drive the scroll-spy + <h3 id> anchors).
  help: {
    eyebrow: "User guide",
    heading: "Help",
    lead: "How Cluckwork works, screen by screen — and how to undo mistakes.",
    contentsAriaLabel: "Help contents",
    contentsEyebrow: "Contents",

    // Contents-rail labels (TOC array, 2nd element). Order mirrors the <h3
    // id=...> sections below — see the KEEP comment at the top of
    // HelpPage.tsx. tocGlossary is the rail's link text only; the glossary
    // SECTION itself (heading + 37-row table + closing note) is externalized
    // further down, near the end of this block (Task 33, B6b).
    tocGettingAround: "Getting around",
    tocSigningIn: "Signing in",
    tocDailyLoop: "The daily loop",
    tocRoles: "Who can do what",
    tocDialogs: "Adding & correcting",
    tocDailyEntry: "Daily entry",
    tocFlocks: "Flocks & birds",
    tocGrades: "Egg grades",
    tocProducts: "Products",
    tocStock: "Stock",
    tocInventory: "Feed & inventory",
    tocWater: "Water",
    tocSales: "Customers & sales",
    tocReports: "Reports",
    tocExpenses: "Expenses",
    tocHistory: "History",
    tocAudit: "Audit log",
    tocExport: "Export & backup",
    tocFarmSettings: "Farm settings",
    tocFarmPalette: "Farm palette",
    tocInstall: "Install on a phone",
    tocMistakes: "Fixing mistakes",
    tocGlossary: "Glossary",

    // Getting around
    gettingAroundHeading: "Getting around",
    gettingAroundSidebar:
      "On a computer, every screen sits in the <strong>sidebar</strong> on the left, grouped by job.",
    gettingAroundTabs:
      "On a phone, the screens you use most are <strong>tabs across the bottom</strong>, in easy thumb reach. "
      + "Which four you get depends on your role — a worker gets Daily entry, someone in sales gets Sales. "
      + "Everything else is one tap away under <strong>More</strong>.",
    gettingAroundErrorScreen:
      "If a screen ever shows <strong>\"Something went wrong\"</strong>, that is the app catching an error "
      + "instead of leaving you on a blank page. Anything you had already saved is safe (anything you were "
      + "still typing may need to be entered again) — tap <strong>Reload</strong>, or <strong>Back to the "
      + "dashboard</strong> and try again. If it keeps happening, open \"Error details\" and send a screenshot.",

    // Signing in
    signingInHeading: "Signing in",
    signingInBasic:
      "Sign in with the email and password your administrator set up. A wrong password just says "
      + "<strong>Invalid email or password</strong> — try again.",
    signingInRateLimit:
      "To slow down anyone guessing passwords, sign-in attempts from the same place are <strong>limited</strong>. "
      + "After too many tries in a few minutes you'll see <strong>\"Too many sign-in attempts\"</strong> — that "
      + "isn't a fault, just wait a few minutes and try again. Being <em>signed in</em> already is never "
      + "affected; your work carries on normally.",
    signingInAccountLock:
      "Separately, too many wrong passwords for <em>one account</em> briefly lock <em>that</em> account. While "
      + "it is locked, even the correct password still says <strong>Invalid email or password</strong>. The "
      + "lock is temporary — wait up to about 15 minutes and try again.",
    signingInPersistence:
      "Your sign-in is kept in your browser securely and stays active as you work, even across reloads and "
      + "with the app open in <strong>several tabs</strong> at once. After the app is <strong>updated</strong> "
      + "you may be asked to sign in once more — that's expected.",
    // #283 — first-run provisioning: no default credential ever ships with
    // the app, so the very first sign-in always starts from a printed
    // one-time password.
    signingInFirstRun:
      "<strong>First sign-in on a brand-new farm.</strong> There is no default password — an operator runs a "
      + "one-time setup command that prints a temporary one. Sign in with it and you'll immediately land on a "
      + "<strong>Set your password</strong> screen instead of the normal app; nothing else works until you pick "
      + "your own password there. This is separate from an ordinary <em>Change password</em>. Until that setup "
      + "step has been run, trying to sign in tells you so and points you at whoever administers the server, "
      + "rather than claiming your details were wrong.",
    // #308 — step-up re-confirmation for the two sensitive user-administration
    // actions. Deliberately does NOT mention "grant"/"token" — that's internal
    // mechanism, not user-facing language.
    signingInStepUp:
      "Two actions on the <strong>Users</strong> screen ask you to <strong>re-enter your current password</strong> "
      + "right there in the dialog: creating another Owner, and resetting an existing Owner's password. This "
      + "confirms it's really you before handing out that much access — every other action on that screen "
      + "(creating a Worker/Manager/Sales/Read-only user, resetting one of their passwords) does not ask again.",
    signingInCredentialEpoch:
      "When an administrator resets a password, your existing sign-in can be invalidated immediately. If you "
      + "see a message that your credentials changed, sign in again with your current password.",
    interfaceLanguage:
      "<strong>Interface language.</strong> Everyone can choose the language the interface is shown in from "
      + "<strong>Account → Preferences</strong> — English, Español, or Tagalog. Translation is a work in "
      + "progress: the login and sales screens, error messages, and <strong>Account → Preferences</strong> "
      + "itself are translated today; the rest of the Account screen (including the password section) and the "
      + "rest of the app are being translated screen by screen. Until a screen is translated it simply shows "
      + "in English, whatever language you picked.",

    // The daily loop
    dailyLoopHeading: "The daily loop",
    dailyLoopChain:
      "Everything in Cluckwork hangs off one chain: you record a <strong>daily entry</strong> for each flock "
      + "(eggs by grade, losses, deaths), you <strong>submit</strong> it, and submitting creates dated "
      + "<strong>egg lots</strong> — that's your sellable <strong>stock</strong>. A <strong>sales "
      + "order</strong> takes from stock when you confirm it, always oldest eggs first. Feed flows the same "
      + "way on the input side: purchases put feed into stock, daily usage draws it down per flock.",
    dailyLoopSummary: "Record entry → submit → egg lots → stock → order → confirm.",

    // Who can do what
    rolesHeading: "Who can do what",
    rolesWorkers:
      "Five kinds of sign-in. <strong>Workers</strong> run the daily loop — record and submit entries, "
      + "receive feed, record feed and water usage, create flocks and customers, take orders from draft "
      + "through confirm. A worker can be narrowed to <strong>assigned flocks</strong>: with no assignments "
      + "they can record for any flock; the first assignment restricts them to the listed ones.",
    rolesManagers:
      "<strong>Managers</strong> do everything workers do, plus everything that <strong>undoes, corrects, or "
      + "configures</strong>: voids, stock and water corrections, flock lifecycle, culls, the "
      + "grade/product/item catalogs, expenses, money reports, the audit log, and exports.",
    rolesSalesReadOnly:
      "<strong>Sales</strong> sign-ins handle customers, orders, and <strong>payments</strong> — but no "
      + "production capture and no expenses. <strong>Read-only</strong> sign-ins see stock, history, and "
      + "reports, and can change nothing.",
    rolesAdmin:
      "<strong>Admin (owner)</strong> does everything a manager does and is the only role that manages users: "
      + "creating sign-ins on the <strong>Users</strong> screen (email, password, an optional name, and role) "
      + "and assigning workers to flocks. A user's name can be changed later from the row's <strong>edit</strong> "
      + "action, and the <strong>password</strong> action sets a forgotten password without needing the old "
      + "one. Changing an existing user's role comes with a later release. Controls you can't use are hidden, "
      + "and the server refuses them regardless.",
    ownPassword:
      "<strong>Your own password.</strong> Anyone, in any role, can change their own password on the "
      + "<strong>Account</strong> screen by entering the current one and a new one (at least 12 characters). "
      + "Changing your own password keeps this device signed in with fresh credentials and ends every "
      + "<em>other</em> open session on its next request. If an admin sets your password instead, every one "
      + "of your open sessions ends on its next request.",

    // Adding & correcting
    dialogsHeading: "Adding & correcting",
    dialogsPopup:
      "Adding and correcting happen in a popup. Look for the <strong>New …</strong> button beside the "
      + "screen's title — new grade, product, customer, flock, item, user, order. Each row's "
      + "<strong>edit</strong> or <strong>correct</strong> link opens the same popup with that row's values "
      + "already filled in.",
    dialogsDrillDowns:
      "Drill-downs work the same way. Open a flock's <strong>birds</strong> ledger to record a cull, an "
      + "inventory item to record a purchase, feed usage, or a stock correction, an order to <strong>record a "
      + "payment</strong>, or a worker's <strong>flocks</strong> to manage their access — the ledger stays "
      + "where it is and the form comes to you.",
    dialogsCancel:
      "<strong>Cancel</strong>, Escape, or a click outside closes the popup, records nothing, and clears "
      + "what you typed — reopen it and you start from a blank form. If a save fails, the popup stays open "
      + "with your values and the reason, so you can fix it and try again — retrying is safe, it never "
      + "records the same thing twice.",
    dialogsInlineForms:
      "The screens whose whole job is capture keep their form on the page: <strong>Daily entry</strong>, "
      + "<strong>Water</strong>, recording an expense, and adding lines to a draft order. Those you use every "
      + "day — no extra click.",
    dialogsSteppers:
      "Whole-number counts — egg counts, bird counts, sale quantities, eggs per unit — have thumb-sized "
      + "<strong>−</strong> and <strong>+</strong> buttons: tap for one, <strong>hold</strong> to speed up. "
      + "A sale line's quantity never steps below 1. Prices and fractional amounts are still typed.",
    dialogsConfirm:
      "<strong>Actions that cannot be undone ask first.</strong> Submitting a day, confirming or cancelling "
      + "an order, depleting or archiving a flock — each one says what is about to happen and waits. The "
      + "keyboard starts on <strong>Cancel</strong>, so pressing Enter by habit never goes through with it. A "
      + "<strong>red</strong> button means the action undoes or retires something: voiding, cancelling a "
      + "draft, depleting, archiving. Submitting a day and confirming an order cannot be taken back either, "
      + "but they are the normal way through the week.",
    dialogsVoidReason:
      "<strong>Voids need a reason.</strong> Voiding a daily entry, a payment, or a confirmed order asks the "
      + "same way but wants a written reason first — it is stored with the void and shown wherever that "
      + "record turns up afterwards, so write what actually happened. Leave it empty and the popup says so on "
      + "the spot, keeping whatever you typed.",

    // Daily entry
    dailyEntryHeading: "Daily entry",
    dailyEntryPanes:
      "Pick the flock and date at the top, then work through two panes side by side: <strong>1 Egg "
      + "counts</strong> (total, cracked, dirty, discarded, deaths) and <strong>2 Grading</strong>. The counts "
      + "produce a <strong>sellable</strong> figure, and that is the number the grades have to add up to. A "
      + "draft can leave that partly done, or not started at all — submitting needs it exact.",
    dailyEntryGradingDown:
      "Grading counts <strong>down</strong>. Beside the grades is how many sellable eggs you still have to "
      + "place; it turns green the moment the day adds up and red if you go over. You cannot submit until it "
      + "reads exactly zero — grading a day partway, or not at all, is fine for a draft but not for Submit.",
    dailyEntryButtons:
      "Every count has <strong>−</strong> and <strong>+</strong> buttons. Tap for one, or <strong>hold</strong> "
      + "— it speeds up as you go, so a few hundred eggs takes about a second. Easier than a keypad with "
      + "gloves on. A grade's <strong>+</strong> stops once the day is fully graded, so you cannot overshoot "
      + "with it.",
    dailyEntryPutAllIn:
      "Most days end the same way — one grade takes whatever is left. <strong>Put all in…</strong> beside the "
      + "remaining count does it in one move: drag it onto a grade, or tap it and pick one.",
    dailyEntrySaveBar:
      "Both save buttons stay in a bar at the bottom of the screen as you scroll. On a phone that bar also "
      + "shows the sellable count and how many are left, so you never lose sight of whether the day adds up.",
    dailyEntrySaveSubmit:
      "<strong>Save draft</strong> keeps the day editable. <strong>Submit</strong> makes it official: it "
      + "creates the day's egg lots and records deaths in the flock's bird ledger. Workers can no longer edit "
      + "it — an admin can adjust or void it (see \"Fixing mistakes\").",
    dailyEntryLocking:
      "Submitted entries <strong>lock automatically after 7 days</strong>. Locked only means the correction "
      + "window for routine fixes has passed — admin adjust/void still works on locked entries.",
    dailyEntryToday:
      "\"Today\" means <strong>your farm's today</strong>, not the clock in some other part of the world. You "
      + "can record any day up to and including it; a day that has not happened yet on the farm is refused — "
      + "here and everywhere else you enter a date: feed and water use, feed purchases and stock corrections, "
      + "expenses, payments, and a flock's placement date. The same date decides when eggs come out of a "
      + "withdrawal period, which eggs a sale can take, the day a flock is depleted or archived on, and the "
      + "range reports open on — so nothing disagrees about what day it is.",
    dailyEntryOnePerDay:
      "One entry per flock per day. Reopening a day that has a draft loads it for editing and shows an "
      + "<strong>Editing draft</strong> badge beside the title, so picking up saved work never looks like "
      + "starting fresh. If prefill fails, saving is blocked until it succeeds so an existing draft is never "
      + "silently overwritten.",
    dailyEntryDepletedBackfill:
      "Depleted flocks accept backfilled entries up to their depletion date; archived flocks accept none.",

    // Flocks & birds
    flocksHeading: "Flocks & birds",
    flocksCurrentBirds:
      "A flock's <strong>current birds</strong> = its starting count minus everything in its <strong>bird "
      + "ledger</strong>: deaths (added automatically when entries are submitted), <strong>culls</strong> "
      + "(birds deliberately removed — sold, slaughtered, given away), and manual <strong>adjustments</strong> "
      + "(count corrections, either direction).",
    flocksLifecycle:
      "Lifecycle: <strong>Active</strong> (normal) → <strong>Depleted</strong> (birds gone; history stays, "
      + "backfill allowed) → <strong>Archived</strong> (hidden from daily work). Depleting and archiving ask "
      + "for confirmation; both are reversible with <strong>Reactivate</strong>.",
    flocksPermissions:
      "Anyone can create a flock and view the bird ledger. Editing a flock, lifecycle changes, and recording "
      + "culls/adjustments are admin-only.",

    // Egg grades
    gradesHeading: "Egg grades",
    gradesBuckets:
      "Grades are your farm's grading buckets — sizes (Large…), qualities (Cracked…), or custom. Only "
      + "<strong>saleable</strong> grades appear in entry capture and on orders; non-saleable buckets are "
      + "bookkeeping.",
    gradesDeactivating:
      "Grades are never deleted. <strong>Deactivating</strong> removes a grade from capture and order "
      + "pickers: its stock stays counted and order lines added earlier can still confirm, but it can't be "
      + "put on <em>new</em> order lines — reactivate the grade to sell remaining stock. History keeps "
      + "showing its name.",
    gradesAdminOnly: "The grade catalog is configuration — managing it is admin-only.",

    // Products
    productsHeading: "Products (admin)",
    productsWhatYouSell:
      "Products are what you sell — \"Large Eggs by the dozen\", \"Mixed carton\". Each egg product points "
      + "at an egg grade (that's where its stock comes from) and carries a selling unit and an optional "
      + "default price. Only egg products exist for now.",
    productsPackedUnits:
      "<strong>Packed units</strong> set how many eggs each unit holds — your carton might be 12, 18, or 30. "
      + "Changing a unit only affects future sales; past orders keep the count they were sold with.",

    // Stock
    stockHeading: "Stock",
    stockLots:
      "Every grade expands into its <strong>lots</strong> (one per submitted day), and every lot into its "
      + "<strong>movement ledger</strong> — an explicit line for each production, sale, correction, or void. "
      + "The running sum always equals the balance shown; nothing changes stock without leaving a line.",
    stockRestricted:
      "Stock is the sum of your egg lots per grade. The <strong>restricted</strong> column is reserved for "
      + "medication withholding periods — that feature arrives with medication tracking. <strong>Nothing "
      + "marks eggs restricted yet, so the system does not enforce withdrawal times today</strong> — manage "
      + "withholding periods outside Cluckwork for now.",
    stockFifo: "Selling always takes the oldest lots first, so stock naturally rotates.",

    // Feed & inventory
    inventoryHeading: "Feed & inventory",
    inventoryItems:
      "<strong>Items</strong> define what you track (feed, supplements…) and the unit it's measured in. The "
      + "unit locks once stock has been received — quantities on record must keep meaning what they meant.",
    inventoryPurchaseUsage:
      "<strong>Record purchase</strong> books received stock as a dated lot with its cost. <strong>Record "
      + "usage</strong> logs what a flock ate on a day: it draws from the oldest lots first (only lots that "
      + "existed on that date) and estimates the cost from the actual lots consumed.",
    inventoryLedger:
      "Every change lands in the item's <strong>movement ledger</strong> — purchases, usage, corrections. "
      + "Ledger rows are never edited or deleted.",
    inventoryCorrections:
      "Typos and spoilage are fixed with <strong>corrections</strong>: an <em>Adjustment</em> (either "
      + "direction) or a <em>Discard</em> (write-off) against a specific lot, always with a reason. The "
      + "original row and the correction both stay visible.",
    inventoryPermissions:
      "Recording purchases and usage is open to everyone; the item catalog and stock corrections are "
      + "admin-only.",

    // Water
    waterHeading: "Water",
    waterRecording:
      "Record what each flock drank per day: either a direct amount (liters or gallons) or <strong>meter "
      + "readings</strong> — the amount is then the meter delta (end − start).",
    waterCorrecting:
      "Water records have no stock behind them, so mistakes are fixed by <strong>correcting the record "
      + "directly</strong> (the \"correct\" button, admin-only) — no compensating entries. The flock and date "
      + "are fixed: picked wrong, record it again under the right one.",
    waterLifecycle:
      "Same lifecycle rule as everywhere: depleted flocks accept backfill up to their depletion date, "
      + "archived flocks accept nothing.",

    // Customers & sales
    salesHeading: "Customers & sales",
    salesDrafts:
      "Orders start as <strong>drafts</strong>: add lines by picking a <strong>product</strong>, a packed "
      + "unit (dozen, carton, …), a quantity, and a price per unit (prefilled from the product's default) — "
      + "edit freely, or <strong>cancel</strong> (the draft is kept, read-only). Each line remembers how many "
      + "eggs its unit held when it was added, so redefining a carton later never changes old orders.",
    salesConfirming:
      "<strong>Confirming</strong> an order allocates real stock — oldest lots first — and is the point "
      + "where inventory changes hands.",
    salesVoiding:
      "A mistaken confirm is undone with <strong>Void</strong> (admin-only, reason required): the eggs go "
      + "back to the exact lots they came from, and the order stays listed as Voided. Voiding is for "
      + "mistakes, not for returns of delivered goods. (Orders confirmed before lot-level allocation tracking "
      + "existed can't self-serve void — ask your administrator.)",
    salesPayments:
      "<strong>Payments</strong> (Sales, Manager, or admin — voiding a payment is admin/manager only): a "
      + "confirmed order's panel shows its settlement history — record partial payments (date, amount, "
      + "method, optional reference) until the outstanding amount reaches zero; overpaying is refused. A "
      + "wrong payment is <strong>voided</strong> (reason required) and the outstanding grows back. An order "
      + "with payments can't be voided until its payments are voided first. The Customers page shows each "
      + "customer's outstanding balance.",

    // Reports
    reportsHeading: "Reports",
    reportsProduction:
      "<strong>Production</strong> (everyone): pick a date range — per-day eggs, losses, sellable, deaths, "
      + "and <strong>hen-day %</strong> (eggs collected ÷ birds alive that day × 100), with period totals and "
      + "a by-grade breakdown. Draft and voided entries don't count.",
    reportsMoney:
      "<strong>Money</strong> (admin): sales summary for the range's orders (revenue / paid / outstanding), "
      + "expenses by category, and <strong>basic profit</strong> — confirmed revenue minus recorded expenses, "
      + "no cost-of-goods.",
    reportsThrottle:
      "<strong>If a report is refused</strong>: the farm runs only a few reports at a time, so one "
      + "busy screen cannot slow the app for everyone else. Asking for several at once — a few people "
      + "opening Reports together, or repeated retries — can come back as <strong>try again shortly</strong> "
      + "instead of a table. Nothing was recorded and nothing was lost: press <strong>retry</strong> "
      + "on the Reports screen a moment later and it re-runs with the same dates you picked.",

    // Expenses
    expensesHeading: "Expenses (admin)",
    expensesRecording:
      "Record money going out: date, category, description, and amount (in the farm's currency), optionally "
      + "tied to a flock. The month picker shows a running total; categories are managed on the same screen "
      + "(deactivating one hides it from new expenses — recorded ones keep it).",
    expensesCorrections:
      "Corrections edit the expense in place (<strong>correct</strong> on the row). If someone else "
      + "corrected it first, the form reloads their values and asks you to re-apply. The currency an expense "
      + "was recorded in never changes.",
    expensesAdminOnly:
      "Expenses are money data, so the whole screen — viewing included — is admin-only, unlike the "
      + "production screens where workers record.",

    // History
    historyHeading: "History",
    historyBrowse:
      "Browse recorded daily entries newest-first, filtered by flock and date range. The status column shows "
      + "the entry's life: Draft, Submitted, Locked (7+ days old), Adjusted (hover for the reason), or "
      + "Voided.",
    historyAdminActions:
      "Admins correct from here: <strong>adjust</strong> reopens the entry in the same two-step form as "
      + "Daily entry — same sellable count, same grading chip, same <strong>put all in…</strong> shortcut "
      + "— with a reason required; <strong>void</strong> undoes the whole entry. Stock and the bird ledger "
      + "follow automatically.",
    historyDraftEdit:
      "Draft rows have an <strong>edit</strong> link (everyone, not just admins) that jumps back to the "
      + "Daily entry screen with that flock and day loaded — drafts are edited there, not adjusted.",

    // Audit log
    auditHeading: "Audit log (admin)",
    auditLog:
      "Every corrective, destructive, or configuration change lands in the audit log automatically: who did "
      + "it, when (UTC), what it touched, and the reason where one was given. Written together with the "
      + "change itself — a failed action leaves no trace, a successful one always does — and never editable, "
      + "by anyone.",

    // Export & backup
    exportHeading: "Export & backup (admin)",
    exportCsv:
      "The Export screen downloads your data as CSV files you can open in any spreadsheet — one dataset at a "
      + "time, or everything at once as a zip (the <strong>full backup</strong>, with a manifest of row "
      + "counts). Keep a copy somewhere safe on your own schedule; automatic scheduled backups come in a "
      + "later phase.",
    exportFormats:
      "Money columns hold minor units (cents) plus the currency — exact values, not display formatting. "
      + "Dates are ISO (YYYY-MM-DD), and timestamps are UTC.",

    // Farm settings
    farmSettingsHeading: "Farm settings (admin)",
    farmSettingsIntro:
      "<strong>Setup → Farm settings</strong> holds the farm's name and the four things that decide how "
      + "everything reads: <strong>timezone</strong>, <strong>locale</strong>, <strong>currency</strong> and "
      + "<strong>unit system</strong>. First day of week and the date and time formats are optional — leave "
      + "them blank and the locale decides. The timezone takes effect everywhere the moment it is saved; the "
      + "rest are recorded against the farm and will drive how amounts, dates and measurements are displayed "
      + "once that formatting lands.",
    farmSettingsTimezone:
      "The <strong>timezone</strong> is the farm's day. Every field that records <em>when something "
      + "happened</em> — daily entry, flocks, water, feed usage and purchases, expenses, orders and payments "
      + "— opens on it and refuses to go past it, whatever day the phone or laptop in your hand is on, so a "
      + "device travelling ahead of the farm can no longer offer a date the save then refuses. Dates that are "
      + "meant to be in the future are not capped: a feed batch's <strong>expiry</strong>, and the date "
      + "ranges you filter History and Water by.",
    farmSettingsCurrency:
      "The <strong>currency</strong> locks the moment the farm records its first amount — a sale, a payment, "
      + "an expense, a priced product, or money spent on feed. The field shows as locked with the reason "
      + "rather than letting you type a code that would be refused. Nothing already recorded is ever "
      + "re-priced, which is exactly why it locks.",
    farmSettingsLogo:
      "The <strong>logo</strong> replaces the Cluckwork mark in the sidebar for everyone at the farm. PNG, "
      + "JPEG or WebP, up to the size limit shown on the screen (2 MB by default) and 4096 pixels a side. "
      + "Animated images are refused rather than flattened. What gets stored is a rebuilt copy: camera and "
      + "location details are stripped out on the way in — a photo taken on a phone carries where it was "
      + "taken, and for a farm that is its address. Remove it and the sidebar goes back to the Cluckwork mark.",
    farmSettingsSquareLogo:
      "Use a <strong>square</strong> logo. It shows small in the sidebar, so a simple, tightly-cropped mark "
      + "— a symbol or a single letter — reads much better there than a wide wordmark or a detailed picture, "
      + "which shrink to something unreadable. Keep a detailed logo for print or a website; give the app a "
      + "clean little mark.",

    // Farm palette
    farmPaletteHeading: "Farm palette",
    farmPaletteIntro:
      "Farm settings lets an admin pick the accent colour used across the app for everyone on the farm: "
      + "Aubergine, Forest, Slate or Terracotta. The choice applies when you save, and everyone sees it the "
      + "next time their app loads.",
    farmPaletteLightNight:
      "Light and night mode are separate and personal. Each person picks their own with the toggle in the "
      + "sidebar, on each device, and the farm palette never overrides it — every palette is designed to work "
      + "in both.",

    // Install on a phone
    installHeading: "Install on a phone",
    installIntro:
      "Cluckwork can be added to a phone or tablet's home screen, where it gets its own icon and opens in "
      + "its own window without the browser bars — more room for the entry screens and quicker to reach in "
      + "the shed. It's the same app, not a separate download, so there is nothing to update from an app "
      + "store.",
    installSteps:
      "<strong>Android (Chrome):</strong> open the menu and choose <strong>Install app</strong> or "
      + "<strong>Add to Home screen</strong>. <strong>iPhone/iPad (Safari):</strong> tap <strong>Share</strong>, "
      + "then <strong>Add to Home Screen</strong>.",
    installHttps:
      "Installing is only offered over a secure (<strong>https</strong>) address. If your farm reaches "
      + "Cluckwork on a plain <strong>http</strong> one the option simply won't appear — nothing is broken, "
      + "and the app works exactly as it does in the browser.",
    installOffline:
      "Installing does <strong>not</strong> make the app work offline. It still needs a connection to load "
      + "and save; only the app's own screens are kept on the device so it starts quickly. Recording while "
      + "offline is planned work, not something installing turns on.",
    installNewVersion:
      "When a new version is released you'll see <strong>\"A new version of Cluckwork is ready\"</strong>. "
      + "It waits for you rather than reloading while you're typing — press <strong>Reload</strong> when "
      + "you're at a good moment, or <strong>Later</strong> and it will ask again next time. Nothing is lost "
      + "by leaving it.",

    // Fixing mistakes (table cells are prose, part of 6a)
    mistakesHeading: "Fixing mistakes",
    mistakesIntro:
      "Every fix in this table needs an admin sign-in (see \"Who can do what\") — workers record, admins "
      + "correct. The one exception: a <em>draft</em> is still recording, not correcting, so workers edit "
      + "their own drafts.",
    mistakesTableMistakeHeader: "Mistake",
    mistakesTableFixHeader: "Fix",

    mistakesRow1Mistake: "Depleted or archived the wrong flock",
    mistakesRow1Fix: "Flocks → <strong>Reactivate</strong> (fully reversible)",

    mistakesRow2Mistake: "Wrong bird count",
    mistakesRow2Fix: "Flocks → bird ledger → <strong>Adjustment</strong> (either direction)",

    mistakesRow3Mistake: "Confirmed the wrong sales order",
    mistakesRow3Fix:
      "Sales → open the order → <strong>Void order</strong> (stock returns to its lots; reason required). "
      + "If payments were recorded on it, void those first.",

    mistakesRow4Mistake: "Recorded a wrong payment",
    mistakesRow4Fix:
      "Sales → open the order → payments → <strong>void</strong> (reason required): the row is kept and the "
      + "outstanding amount grows back.",

    mistakesRow5Mistake: "Wrong <em>quantity</em> in a feed purchase / spoiled feed",
    mistakesRow5Fix:
      "Inventory → open the item → <strong>Correct stock</strong> (Adjustment or Discard against the lot; "
      + "reason required). Only quantities are correctable — a wrong cost, date, or lot number can't be "
      + "fixed yet, so double-check those before saving.",

    mistakesRow6Mistake: "Over- or under-recorded feed usage",
    mistakesRow6Fix:
      "Same correction form: a positive Adjustment returns over-used stock to the lot (up to what it "
      + "received); a negative one removes under-recorded stock. The usage record itself and its cost "
      + "estimate stay as recorded — corrections fix the stock, not the history.",

    mistakesRow7Mistake: "Wrong water record",
    mistakesRow7Fix:
      "Water → <strong>correct</strong> on the record — amounts, source, meters, and note edit in place (no "
      + "stock behind water). Flock and date are fixed: picked wrong, record it again under the right one.",

    mistakesRow8Mistake: "Wrong numbers in a <em>submitted</em> daily entry",
    mistakesRow8Fix:
      "History → <strong>adjust</strong> (admin) — totals, losses, mortality, and grade split, with a "
      + "required reason. The corrected grades must add up to the corrected sellable count exactly, the same "
      + "rule Submit uses, and <strong>Save adjustment</strong> is blocked until they do. Stock and the bird "
      + "ledger update to match automatically, but eggs already sold can never be un-counted: shrinking a "
      + "grade below what was sold is refused. The previous values stay visible on the entry.",

    mistakesRow9Mistake: "Entire <em>submitted</em> entry is wrong (wrong flock or day)",
    mistakesRow9Fix:
      "History → <strong>void</strong> (admin, reason required): its egg lots empty, its deaths are "
      + "reversed in the bird ledger, and the entry is kept as Voided. Refused if any of its eggs were "
      + "already sold — void the sale first. Voiding frees the day: the correct entry can then be recorded "
      + "for the same flock and date.",

    mistakesRow10Mistake: "Mistake in a <em>draft</em> entry or order",
    mistakesRow10Fix:
      "Edit it — draft numbers, grade lines, and order lines are all editable (draft entries: History → "
      + "<strong>edit</strong> jumps to the Daily entry screen with the day loaded). The flock/date of an "
      + "entry and the customer/date of an order are fixed, though: picked wrong, just record it again under "
      + "the right one (and cancel the wrong draft order).",

    // Glossary (Task 33, B6b, #182): the 37-row term/definition table plus
    // its closing repo-note paragraph. Externalized byte-faithful from the
    // prior hardcoded English — see the Task 33 brief's alignment note:
    // specs/product/GLOSSARY.md is the canonical 96-term termbase (Task 34
    // reconciles it separately) and this in-app table is a curated subset,
    // so its English wording is intentionally NOT rewritten here. HTML
    // entities in the old JSX (&apos;/&quot;) are real apostrophe/quote
    // characters below — same rendered text, just no entity escaping needed
    // inside a JS string. <Trans>-only rows: glossaryInstallToHomeScreenDef
    // (one <strong>) and glossaryRepoNote (one <code>, whose
    // "specs/product/GLOSSARY.md" path text is literal — the closing note's
    // <Trans> renders it via the components map, not a translated string).
    glossaryHeading: "Glossary",

    glossaryNavigationTerm: "Navigation",
    glossaryNavigationDef:
      "Screens live in the left sidebar on a computer; on a phone the four you use most are tabs across "
      + "the bottom, the rest under More.",

    glossaryOperationalDayTerm: "Operational day",
    glossaryOperationalDayDef:
      "Dates mean your farm's calendar day, worked out from the farm's own timezone rather than a clock "
      + "somewhere else. It is the same \"today\" everywhere: what counts as a future date when you record "
      + "work, when eggs leave a withdrawal period, which eggs a sale can take, the day a flock is depleted "
      + "or archived on, and the range reports open on. Every field that records WHEN SOMETHING HAPPENED "
      + "opens on it and will not go past it, whatever day the device in your hand is on. Dates meant to "
      + "fall in the future are not capped — a feed batch's expiry, and the History and Water filters.",

    glossaryInstallToHomeScreenTerm: "Install to home screen",
    glossaryInstallToHomeScreenDef:
      "Adding Cluckwork to a phone or tablet's home screen from the browser, so it gets its own icon and "
      + "opens in its own window without the browser bars. It is the same app, not a separate download — "
      + "nothing to update from an app store. Only offered over a secure (https) address, and it does "
      + "<strong>not</strong> make the app work offline: it still needs a connection to load and save.",

    glossaryNewVersionReadyTerm: "A new version is ready",
    glossaryNewVersionReadyDef:
      "After a release, an installed app notices the new version in the background and asks before "
      + "switching, rather than reloading while you are typing. Press Reload when convenient, or Later and "
      + "it asks again next time. Nothing is lost by leaving it — the running app keeps working until you "
      + "accept.",

    glossaryTooManySignInAttemptsTerm: "Too many sign-in attempts",
    glossaryTooManySignInAttemptsDef:
      "Sign-in is rate limited to slow password guessing: too many attempts from one place in a few "
      + "minutes are refused with this message until a short cool-off passes. It never affects an already "
      + "signed-in session.",

    glossaryTooManyReportsTerm: "Too many reports at once",
    glossaryTooManyReportsDef:
      "The farm runs only a few reports at the same time, so one busy screen cannot slow the app for "
      + "everyone. Over that, a report comes back asking you to try again shortly instead of waiting in "
      + "line. Nothing is recorded or lost — press retry on the Reports screen a moment later; it "
      + "re-runs with the same dates you picked. Each farm has its own allowance, so "
      + "another farm's reports never use up yours.",
    // #308
    glossaryStepUpAuthTerm: "Step-up authentication",
    glossaryStepUpAuthDef:
      "An extra check on top of being signed in: before creating another Owner or resetting an existing "
      + "Owner's password, the Users screen asks you to re-enter your current password right there in the "
      + "dialog. It confirms it's really you before handing out that much access — every other action on "
      + "that screen does not ask again.",

    glossarySomethingWentWrongScreenTerm: "\"Something went wrong\" screen",
    glossarySomethingWentWrongScreenDef:
      "What a screen shows when it hits an error, instead of going blank. Saved data is safe — anything "
      + "you were still typing may need re-entering; tap Reload or Back to the dashboard. \"Error "
      + "details\" holds the message for a screenshot.",

    glossaryDailyEntryTerm: "Daily entry",
    glossaryDailyEntryDef: "One flock's day: eggs by grade, losses, deaths. Draft until submitted.",

    glossaryEggLotTerm: "Egg lot",
    glossaryEggLotDef:
      "A dated batch of sellable eggs of one grade, created by submitting an entry. Stock is the sum of "
      + "lots.",

    glossaryGradeTerm: "Grade",
    glossaryGradeDef: "A grading bucket (size, quality, or custom). Saleable grades can be sold.",

    glossaryEggMovementLedgerTerm: "Egg movement ledger",
    glossaryEggMovementLedgerDef:
      "The line-by-line history behind an egg lot's balance: production in, sales out, corrections and "
      + "voids signed accordingly.",

    glossaryFifoTerm: "FIFO",
    glossaryFifoDef: "\"First in, first out\" — sales and feed usage always take the oldest stock first.",

    glossaryCullTerm: "Cull",
    glossaryCullDef: "Birds deliberately removed from a flock (sold, slaughtered, given away) — not deaths.",

    glossaryMortalityTerm: "Mortality",
    glossaryMortalityDef: "Deaths, recorded on the daily entry; lands in the bird ledger automatically at submit.",

    glossaryDepleteTerm: "Deplete",
    glossaryDepleteDef: "Mark a flock as having no birds left. History stays; reversible via Reactivate.",

    glossaryArchiveTerm: "Archive",
    glossaryArchiveDef: "Hide a finished flock from daily work. Reversible via Reactivate.",

    glossaryWithdrawalRestrictionTerm: "Withdrawal restriction",
    glossaryWithdrawalRestrictionDef:
      "A hold on eggs during a medication withholding period. Coming with medication tracking — nothing "
      + "sets restrictions yet, so manage withholding periods outside Cluckwork for now.",

    glossaryProductTerm: "Product",
    glossaryProductDef:
      "What you sell — an egg product points at a grade (its stock source) and carries a selling unit and "
      + "default price.",

    glossaryPackedUnitTerm: "Packed unit",
    glossaryPackedUnitDef:
      "How many eggs a dozen/tray/carton/case holds on your farm. Each sale line keeps the count it was "
      + "sold with.",

    glossarySalesLineTerm: "Sales line",
    glossarySalesLineDef:
      "One product on an order: quantity in selling units, priced per unit; the eggs behind it are "
      + "quantity × the unit's egg count.",

    glossaryConfirmOrderTerm: "Confirm (order)",
    glossaryConfirmOrderDef: "Turns a draft order into a real sale and allocates stock. Undone only by voiding.",

    glossaryVoidOrderTerm: "Void (order)",
    glossaryVoidOrderDef:
      "Undo of a mistaken confirm — stock returns to the exact lots it came from. Needs a reason.",

    glossaryCancelOrderTerm: "Cancel (order)",
    glossaryCancelOrderDef: "Close a draft that never happened. No stock involved.",

    glossaryInventoryItemTerm: "Inventory item",
    glossaryInventoryItemDef:
      "A catalog entry for something you stock (feed, supplements…), with a fixed unit of measure.",

    glossaryInventoryLotTerm: "Inventory lot",
    glossaryInventoryLotDef: "One received batch of an item, with its own cost. On-hand = sum of lots.",

    glossaryInventoryMovementLedgerTerm: "Inventory movement ledger",
    glossaryInventoryMovementLedgerDef:
      "The append-only trail of every feed/supply stock change. Corrections are new rows, never edits.",

    glossaryWaterUsageTerm: "Water usage",
    glossaryWaterUsageDef:
      "What a flock drank on a day — direct amount or meter delta. Editable in place; flock/date fixed.",

    glossaryFeedUsageTerm: "Feed usage",
    glossaryFeedUsageDef: "What a flock ate on a day; drains lots FIFO and estimates cost from them.",

    glossaryAdjustmentDiscardTerm: "Adjustment / Discard",
    glossaryAdjustmentDiscardDef:
      "Stock corrections against a lot, reason required. Discard = write-off (spoilage).",

    glossaryRolesTerm: "Roles",
    glossaryRolesDef:
      "Admin (owner), Manager, Worker, Sales, Read-only — see \"Who can do what\". Workers record; "
      + "managers also correct and configure; sales handles orders and payments; read-only just views.",

    glossaryLockedEntryTerm: "Locked (entry)",
    glossaryLockedEntryDef:
      "A submitted entry older than 7 days — closed to routine edits; admin adjust/void still works.",

    glossaryAdjustEntryTerm: "Adjust (entry)",
    glossaryAdjustEntryDef:
      "Admin correction of a submitted entry. The corrected grades must add up to the corrected sellable count "
      + "exactly, the same rule Submit uses — an adjustment has no draft state to leave partly graded. Stock "
      + "and bird ledger reconcile automatically; sold eggs are untouchable; previous values stay visible.",

    glossaryVoidEntryTerm: "Void (entry)",
    glossaryVoidEntryDef:
      "Admin undo of a whole submitted entry — lots empty, deaths reverse, entry preserved as Voided. "
      + "Refused once its eggs are sold.",

    glossaryFarmSettingsTerm: "Farm settings",
    glossaryFarmSettingsDef:
      "The farm's name, timezone, locale, currency and unit system, plus optional first day of week and "
      + "date/time formats. Setup → Farm settings; owners and managers edit, everyone reads — formatting "
      + "money and dates is not a permission.",

    glossaryCurrencyLockTerm: "Currency lock",
    glossaryCurrencyLockDef:
      "The farm currency stops being editable once anything has recorded an amount in it — a sale, a "
      + "payment, an expense, a priced product, money spent on feed. The field shows locked with the "
      + "reason. Nothing already recorded is ever re-priced, which is the whole point.",

    glossaryFarmLogoTerm: "Farm logo",
    glossaryFarmLogoDef:
      "Your own image in place of the Cluckwork mark in the sidebar, uploaded from Farm settings. PNG, "
      + "JPEG or WebP (2 MB by default), still images only; a square, simple mark reads best at the small "
      + "sidebar size. Stored as a rebuilt copy with camera and location details stripped out.",

    glossaryFarmPaletteTerm: "Farm palette",
    glossaryFarmPaletteDef:
      "The farm-wide accent colour, chosen by an admin in Farm settings. Separate from each person's own "
      + "light/night mode setting.",

    glossaryUiLanguageTerm: "UI language",
    glossaryUiLanguageDef:
      "The per-user language the interface is shown in — English, Español, or Tagalog — chosen from "
      + "Account → Preferences. English is the fallback for any screen not yet translated, whatever "
      + "language you picked.",

    glossaryRepoNote:
      "Full spec-language definitions live in the repository's <code>specs/product/GLOSSARY.md</code>.",
  },
} as const;

export type Resources = typeof en;
