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
} as const;

export type Resources = typeof en;
