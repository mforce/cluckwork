// MACHINE-DRAFTED translation, PENDING NATIVE-SPEAKER REVIEW (#182 / epic #15). Keys mirror en.ts exactly.
//
// Translator notes:
// - sales.reference / sales.referenceOptional ("Reference" / "Reference
//   (opsyonal)") intentionally KEEP "Reference" as an English loanword — it is
//   the common term for a payment/transaction reference number in PH sales UIs,
//   not an untranslated oversight.
// - UI screen/label names referenced in prose (e.g. "(Customers page)" in
//   sales.addCustomerFirst) are kept in English, matching the actual on-screen
//   label, until that screen itself is externalized to the catalog (#182).
export const tl = {
  common: {
    cancel: "Kanselahin",
    save: "I-save",
    close: "Isara",
    delete: "Burahin",
    edit: "I-edit",
    add: "Idagdag",
    confirm: "Kumpirmahin",
    loading: "Naglo-load…",
    retry: "subukan ulit",
    required: "Kailangan",
    optional: "Opsyonal",
    actions: "Mga Aksyon",
    search: "Maghanap",
    all: "Lahat",
    none: "Wala",
    yes: "Oo",
    no: "Hindi",
  },
  auth: {
    title: "Cluckwork",
    email: "Email",
    password: "Password",
    signIn: "Mag-sign in",
    signingIn: "Nagsa-sign in…",
    invalidCredentials: "Mali ang email o password.",
    tooManyAttempts:
      "Sobra na ang subok sa pag-sign in. Maghintay ng ilang minuto at subukan ulit.",
    apiDown: "Hindi makapag-sign in. Gumagana ba ang API?",
  },
  account: {
    preferences: "Mga Kagustuhan",
    language: "Wika",
    languageHint: "Ang wikang gagamitin sa interface, para lang sa iyo.",
  },
  errors: {
    "Me.Language.Format": "Dapat 2–8 letrang code ang wika, halimbawa 'en'.",
  },
  sales: {
    // Headings
    title: "Benta",
    loading: "Naglo-load…",
    payments: "Mga Bayad",
    ordersHeading: "Mga Order",

    // Buttons
    newOrder: "Bagong order",
    newDraftOrder: "Bagong draft na order",
    save: "i-save",
    cancelEdit: "kanselahin",
    edit: "i-edit",
    remove: "alisin",
    addLine: "Magdagdag ng linya",
    confirmOrderButton: "Kumpirmahin ang order (maglalaan ng stock)",
    cancelDraft: "Kanselahin ang draft",
    // Intentional screen-specific lowercase variant, distinct from
    // common.close ("Isara") — mirrors en.sales.close (#182).
    close: "isara",
    voidPaymentButton: "i-void",
    recordPayment: "Itala ang bayad",
    voidOrderButton: "I-void ang order (ibabalik ang stock)",
    open: "buksan",
    loadMore: "mag-load pa",

    // Form labels
    customer: "Kustomer",
    date: "Petsa",
    product: "Produkto",
    perLabel: "Bawat",
    quantity: "Dami",
    unitPriceWithCurrency: "Presyo bawat yunit ({{code}})",
    method: "Paraan",
    referenceOptional: "Reference (opsyonal)",
    noteOptional: "Tala (opsyonal)",
    amountWithCurrency: "Halaga ({{code}})",
    status: "Katayuan",

    // Table headers (shared across the items / payments / orders tables)
    qty: "Dami",
    eggs: "Itlog",
    unitPrice: "Presyo bawat yunit",
    lineTotal: "Kabuuan ng linya",
    reference: "Reference",
    amount: "Halaga",
    total: "Kabuuan",

    // aria-labels
    editQuantityAriaLabel: "I-edit ang dami",
    editUnitPriceAriaLabel: "I-edit ang presyo bawat yunit",

    // Status-filter options
    allOption: "Lahat",
    statusDraft: "Draft",
    statusConfirmed: "Kumpirmado",
    statusCancelled: "Kinansela",
    statusVoided: "Na-void",

    // Unit picker (the sale unit, e.g. "3 Dozen") — text equals the enum value.
    // NOTE (flag for native review): several unit/method nouns kept as common
    // English loanwords (Flat, Tray, Case, Carton, Bank Transfer, Mobile
    // Payment) — normal register in PH farm/retail software; confirm which
    // ones, if any, should switch to a Filipino term.
    unitEgg: "Itlog",
    unitDozen: "Dosena",
    unitFlat: "Flat",
    unitTray: "Tray",
    unitCarton: "Karton",
    unitCase: "Case",

    // Payment-method picker — text equals the enum value.
    methodCash: "Cash",
    methodCheck: "Tseke",
    methodCard: "Card",
    methodBankTransfer: "Bank Transfer",
    methodMobilePayment: "Mobile Payment",
    methodOther: "Iba pa",

    // Misc UI text
    addCustomerFirst: "Magdagdag muna ng customer (Customers page), pagkatapos gumawa ng order.",
    noOrdersMatch: "Walang tugmang order.",
    voidingNeedsAdmin: "Kailangan ng admin para mag-void.",
    voidReasonLabel: "Dahilan ng pag-void: {{reason}}",
    orderTotal: "Kabuuan: {{amount}}",
    perUnit: "bawat {{unit}}",
    eggsCount: "({{count}} na itlog)",
    // Interleaves JSX (<strong> around "outstanding …") — rendered via <Trans>.
    paymentsSummary: "Nabayaran {{paid}} — <strong>nakabinbin {{outstanding}}</strong>",

    // Inline validation messages
    enterValidAmount: "Maglagay ng valid na halaga.",
    noDecimalPlaces: "Walang decimal ang currency na ito.",
    atMostDecimals: "Pinakamarami {{count}} decimal para sa currency na ito.",
    enterAmountGreaterThanZero: "Maglagay ng halagang higit sa zero.",
    invalidUnitPrice: "Di-wastong presyo bawat yunit.",
    loadSalesDataFailed: "Hindi na-load ang datos ng benta. Gumagana ba ang API?",
    loadOrdersFailed: "Hindi na-load ang mga order.",
    loadPaymentsFailed: "Hindi na-load ang mga bayad ng order na ito.",

    // Confirm / askReason dialogs (title / body / confirmLabel)
    confirmOrderTitle: "Kumpirmahin ang order na ito?",
    confirmOrderBody:
      "Ang stock ay inilalaan mula sa imbentaryo, ang pinakamatandang lote muna (FIFO). " +
      "Ang isang maling kumpirmasyon ay puwedeng i-undo gamit ang Void, na nagbabalik ng stock.",
    confirmOrderConfirmLabel: "Kumpirmahin ang order",
    cancelDraftTitle: "Kanselahin ang draft na ito?",
    cancelDraftBody: "Magiging kinansela ang order at hindi na ito puwedeng i-edit o kumpirmahin.",
    voidPaymentTitle: "I-void ang bayad na ito?",
    voidPaymentBody: "Tataas muli ang nakabinbin na halaga ng order ayon sa halaga ng bayad.",
    voidPaymentConfirmLabel: "I-void ang bayad",
    voidOrderTitle: "I-void ang nakumpirmang order na ito?",
    voidOrderBody: "Ang inilaang stock ay babalik sa eksaktong lote ng itlog na pinagmulan nito.",
    voidOrderConfirmLabel: "I-void ang order",

    // Templated success messages
    orderConfirmed: "Nakumpirma ang order {{ref}} — nailaan ang stock (FIFO).",
    draftOrderCancelled: "Nakansela ang draft na order.",
    paymentRecorded: "Naitala ang bayad.",
    paymentVoided: "Na-void ang bayad — tumaas muli ang nakabinbin na halaga.",
    orderVoided: "Na-void ang order {{ref}} — naibalik ang stock sa imbentaryo.",
  },
} as const;
