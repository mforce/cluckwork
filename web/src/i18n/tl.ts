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

    // machine-drafted (#182) — pending native review. Task 25 (B4): the rest
    // of AccountPage. Keys mirror en.ts exactly, including {{role}}/{{min}}
    // placeholders and the <strong> tag in roleLine.
    // "Account" kept as an English loanword (same treatment as
    // sales.reference above) — the common term in PH tech UIs; flag for
    // native review to confirm rather than assume.
    heading: "Account",
    roleLine: "Naka-sign in ka gamit ang tungkuling <strong>{{role}}</strong>.",
    changePasswordHeading: "Palitan ang password",
    changePasswordHint:
      "Kapag pinalitan mo ang iyong password, ma-si-sign out ka sa lahat "
      + "ng ibang device — mananatiling naka-sign in ang device na ito.",
    currentPasswordLabel: "Kasalukuyang password *",
    newPasswordLabel: "Bagong password (min {{min}} na karakter) *",
    confirmPasswordLabel: "Kumpirmahin ang bagong password *",
    changePasswordButton: "Palitan ang password",
    passwordMismatchError: "Hindi magkatugma ang mga bagong password.",
    passwordTooShortError: "Dapat hindi bababa sa {{min}} na karakter ang bagong password.",
    passwordChangedMessage: "Napalitan ang password. Na-sign out na ang ibang mga device.",
  },
  errors: {
    "Me.Language.Format": "Dapat 2–8 letrang code ang wika, halimbawa 'en'.",
  },

  // machine-drafted (#182) — pending native review. Task CT1 (B1 catch-up):
  // backfilling tl for the shared nav chrome (previously English-only under
  // the now-dropped English-first policy). Keys mirror en.ts nav exactly.
  // Several short abstract nouns kept as English loanwords (Overview,
  // Insights, Setup, Dashboard, Stock, History, Audit, Export, Account) —
  // same treatment as sales.reference/settings.timezoneLabel above, no
  // settled short Filipino equivalent in common PH tech UI use; flag for
  // native review.
  nav: {
    // Section headings (NavGroup.labelKey).
    groupOverview: "Overview",
    groupProduction: "Produksyon",
    groupSalesStock: "Benta at Stock",
    groupInsights: "Insights",
    groupSetup: "Setup",
    groupYou: "Ikaw",
    groupHelp: "Tulong",

    // Destination labels (NavEntry.labelKey).
    dashboard: "Dashboard",
    dailyEntry: "Araw-araw na Tala",
    flocks: "Mga Kawan",
    water: "Tubig",
    inventory: "Imbentaryo",
    stock: "Stock",
    customers: "Mga Customer",
    sales: "Benta",
    history: "History",
    reports: "Mga Report",
    expenses: "Mga Gastos",
    farmSettings: "Mga setting ng bukid",
    grades: "Mga Grado",
    products: "Mga Produkto",
    users: "Mga User",
    audit: "Audit",
    export: "Export",
    account: "Account",
    // Distinct key from groupHelp above, same coincidental-equal-text
    // treatment as en.ts's own comment describes.
    help: "Tulong",

    // AppLayout chrome.
    skipToContent: "Lumaktaw papunta sa pangunahing content",
    primaryNavAriaLabel: "Pangunahin",
    signOut: "Mag-sign out",
    farmLoadFailedNeverLoaded:
      "Hindi na-load ang mga setting ng bukid na ito, kaya susundin ng mga "
      + "petsa ang device na ito sa halip na ang bukid.",
    farmLoadFailedStale:
      "Hindi ma-refresh ang mga setting ng bukid na ito, kaya posibleng "
      + "luma na ang nakikita mo rito.",
    tryAgain: "Subukan Ulit",
    titleSuffix: " — Cluckwork",

    // BottomNav chrome.
    tabBarAriaLabel: "Mga Seksyon",
    moreButton: "Higit Pa",
    menuTitle: "Menu",
    allSectionsAriaLabel: "Lahat ng seksyon",
  },

  // machine-drafted (#182) — pending native review. Task CT1 (B1 catch-up):
  // backfilling tl for NumberField's stepper buttons. {{label}} is the
  // caller-supplied field name, interpolated not translated.
  numberField: {
    increaseLabel: "Dagdagan ang {{label}}",
    decreaseLabel: "Bawasan ang {{label}}",
  },

  // machine-drafted (#182) — pending native review. Task CT1 (B1 catch-up):
  // backfilling tl for the app/screen error-boundary fallback UI.
  errorBoundary: {
    title: "May Nangyaring Mali",
    screenBody:
      "Nagkaproblema ang screen na ito at hindi natapos mag-load. Ligtas "
      + "ang anumang na-save mo na, pero maaaring kailanganin mong i-type "
      + "ulit ang kahit anong tina-type mo pa dito. Gumagana pa rin ang "
      + "ibang bahagi ng app.",
    appBody:
      "Nagkaproblema ang app at hindi natapos mag-load. Kadalasang "
      + "naaayos ito ng pag-reload.",
    reload: "I-reload",
    backToDashboard: "Bumalik sa dashboard",
    detailsSummary: "Mga detalye ng error",
  },

  // machine-drafted (#182) — pending native review. Task CT1 (B1 catch-up):
  // backfilling tl for the light/night mode toggle. "Light"/"Night" (mode
  // names) kept as English loanwords — flag for native review.
  themeToggle: {
    switchToLightMode: "Lumipat sa light mode",
    switchToNightMode: "Lumipat sa night mode",
    light: "Light",
    night: "Night",
  },

  // machine-drafted (#182) — pending native review. Task CT1 (B1 catch-up):
  // backfilling tl for the shared useConfirm dialog's reason field.
  // "Dahilan" matches the existing sales.voidReasonLabel precedent.
  useConfirm: {
    reasonLabel: "Dahilan *",
    reasonRequired: "Kailangan ng dahilan.",
  },

  // machine-drafted (#182) — pending native review. Task CT1 (B1 catch-up):
  // backfilling tl for the service-worker "update ready" banner
  // (UpdatePrompt.tsx, src/pwa).
  pwa: {
    updateAvailable: "May bagong bersyon ng Cluckwork na handa na.",
    reload: "I-reload",
    reloading: "Nire-reload…",
    later: "Mamaya",
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

  // machine-drafted (#182) — pending native review. Task CT2 (B2 catch-up):
  // backfilling tl for the Daily entry capture screen (Task 11, en.ts, batch
  // B2). Keys mirror en.ts dailyEntry exactly, including the
  // {{status}}/{{n}}/{{count}}/{{grade}}/{{losses}}/{{total}}/{{cracked}}/
  // {{dirty}}/{{discarded}} placeholders (no <Trans> tags in this namespace).
  // The entry-locked banner's status word goes through the `enums`
  // statusLabel helper, not a key here.
  dailyEntry: {
    title: "Araw-araw na Tala",

    // Imperative messages
    loadFlocksGradesFailed: "Hindi na-load ang mga kawan/grado. Gumagana ba ang API?",
    deepLinkUnavailable:
      "Ang link na ito para sa pag-edit ay tumuturo sa isang kawan o petsa "
      + "na hindi na available — ginagamit na lang ang mga karaniwang default.",

    // "Editing draft" badge
    editingDraftBadge: "Ina-edit ang draft",

    // Flock + date context row
    flockLabel: "Kawan",
    noFlocksYetOption: "— wala pang kawan —",
    depletedFlockSuffix: " — naubos, para sa pagtatala ng lumang petsa lang",
    dateLabel: "Petsa",
    newFlockButton: "+ bagong kawan",

    // New-flock dialog
    newFlockDialogTitle: "Bagong kawan",
    nameLabel: "Pangalan",
    breedLabel: "Lahi",
    placedLabel: "Paglagay",
    birdsLabel: "Mga Ibon",
    createFlockButton: "Gumawa ng kawan",

    // Locked-day / prefill-failure banners
    entryLockedBanner:
      "Ang araw na ito ay {{status}} na — umiiral na ang mga lote ng itlog "
      + "nito. Ginagawa ang mga pagtatama mula sa History (mga admin: "
      + "i-adjust o i-void).",
    prefillFailedBanner:
      "Hindi na-check kung may tala na ang araw na ito — naka-block ang "
      + "pag-save para hindi ma-overwrite ang umiiral nang datos.",

    // Step headings
    stepLabel: "Hakbang {{n}}",
    stepOfTotal: "ng 2:",
    eggCountsHeading: "Bilang ng Itlog",
    gradingHeading: "Pag-grade",

    // Count field labels
    totalEggsLabel: "Kabuuang Itlog",
    crackedLabel: "Basag",
    dirtyLabel: "Madumi",
    discardedLabel: "Itinapon",
    mortalityLabel: "Pagkamatay",

    // Reconciliation readouts (counts pane)
    countsExceedTotalMessage:
      "Ang basag + madumi + itinapon ({{losses}}) ay lumagpas sa kabuuang "
      + "itlog ({{total}}).",
    sellableLabel: "Nabebenta",
    sellableFormula: "{{total}} − {{cracked}} − {{dirty}} − {{discarded}}",
    deactivatedGradeSuffix: " (naka-deactivate)",

    // Remainder-assignment gesture (grading pane)
    takeRemainderAriaLabel: "Ilagay lahat ng {{count}} na natitira sa {{grade}}",
    takeRemainderButton: "+{{count}}",
    armAriaLabel: "Pumili ng grado para sa {{count}} na natitira",
    disarmAriaLabel: "Kanselahin ang pagpili ng grado",
    armButton: "ilagay lahat sa…",
    disarmButton: "pumili ng grado…",

    // The `grading` derived object's copy (chip + pinned footer)
    fixCountsFirst: "Ayusin muna ang mga bilang",
    fixCountsShort: "ayusin ang mga bilang",
    overSellableCount: "higit sa bilang na nabebenta",
    overShort: "sobra",
    gradedDayAddsUp: "na-grade — tumutugma ang araw",
    allGradedShort: "lahat na-grade",
    leftToGrade: "natitirang i-grade",
    leftShort: "natitira",

    // Pinned footer (phone-only summary + saves)
    countsExceedFooterMessage: "Lumagpas sa kabuuan ang mga nawala — ayusin ang mga bilang",
    sellableWord: "nabebenta",
    saveDraftButton: "I-save ang draft",
    submitButton: "I-save at isumite (gagawa ng lote ng itlog)",

    // Submit confirmation dialog (one-way action, #59)
    confirmSubmitTitle: "Isumite ang araw na ito?",
    confirmSubmitBody:
      "Gagawa ng mga lote ng itlog at hindi na maiedit ang tala. Ang mga "
      + "pagtatama pagkatapos nito ay kailangan ng adjustment ng manager.",
    confirmSubmitLabel: "Isumite ang araw",

    // Save-result messages
    submittedMessage: "Naisumite — {{count}} lote ng itlog ang nagawa.",
    draftSavedMessage: "Na-save ang draft.",
  },

  // machine-drafted (#182) — pending native review. Task CT2 (B2 catch-up):
  // backfilling tl for the Dashboard landing screen (Task 12, en.ts, batch
  // B2). Keys mirror en.ts dashboard exactly, including the {{count}}
  // placeholder (no <Trans> tags in this namespace). "Dashboard"/"Stock"/
  // "Available"/"Restricted"/"Customer" kept as English loanwords — same
  // treatment as nav.dashboard/nav.stock and sales.reference above; flag for
  // native review.
  dashboard: {
    title: "Dashboard",

    // Imperative messages
    loadFailed: "Hindi na-load ang dashboard. Gumagana ba ang API?",
    panelLoadError: "Hindi na-load.",

    // Stat row
    statEggsCollectedToday: "Itlog na Nakolekta Ngayon",
    statEggsAvailable: "Available na Itlog",
    statActiveFlocks: "Aktibong Kawan",

    // "Today" panel (per-flock production)
    todayPanelTitle: "Ngayon",
    noFlocksMessage: "Wala pang kawan — gumawa ng isa sa page na Araw-araw na Tala.",
    flockHeader: "Kawan",
    statusHeader: "Katayuan",
    eggsHeader: "Itlog",
    lossesHeader: "Nawala",
    mortalityHeader: "Pagkamatay",
    noEntryBadge: "walang tala",

    // "Stock" panel (by grade)
    stockPanelTitle: "Stock",
    noStockMessage: "Wala pang stock — magtala at magsumite ng araw-araw na tala.",
    gradeHeader: "Grado",
    availableHeader: "Available",
    restrictedHeader: "Restricted",
    eggsAvailableMessage: "{{count}} itlog ang available.",

    // "Recent sales" panel (hidden for ReadOnly/Denied, #127)
    salesPanelTitle: "Mga Kamakailang Benta",
    noOrdersMessage: "Wala pang order.",
    refHeader: "Ref.",
    customerHeader: "Customer",
    totalHeader: "Kabuuan",
  },

  // machine-drafted (#182) — pending native review. Task CT2 (B2 catch-up):
  // backfilling tl for the Water capture + correction screen (Task 13, en.ts,
  // batch B2). Keys mirror en.ts water exactly, including the {{unit}}
  // placeholder (no <Trans> tags in this namespace). Source/Unit picker
  // values go through the `enums` waterSourceLabel/waterUnitLabel helpers,
  // not a key here.
  water: {
    title: "Tubig",

    // Imperative messages
    loadFlocksFailed: "Hindi na-load ang mga kawan. Gumagana ba ang API?",
    loadRecordsFailed: "Hindi na-load ang mga tala ng tubig.",
    loadMoreFailed: "Hindi na-load ang iba pa.",
    concurrentEditError:
      "Kababago lang ng record na ito sa ibang lugar — i-reload ang "
      + "listahan at subukan ulit.",

    intro:
      "Itala kung ano ang ininom ng bawat kawan — direktang dami, o mga "
      + "reading ng meter (ang dami ay ang delta ng meter). Puwedeng itama "
      + "ang mga tala pagkatapos; naka-fix ang kawan at petsa.",

    // Capture form labels
    flockLabel: "Kawan",
    depletedFlockSuffix: " — naubos, para sa pagtatala ng lumang petsa lang",
    dateLabel: "Petsa",
    sourceLabel: "Pinagmulan",
    unitLabel: "Yunit",
    fromMeterReadingsLabel: "mula sa mga reading ng meter",
    meterStartLabel: "Simula ng Meter",
    meterEndLabel: "Dulo ng Meter",
    quantityLabelWithUnit: "Dami ({{unit}})",
    noteLabel: "Tala",

    // Capture form buttons
    recordWaterButton: "Itala ang tubig",
    saveCorrectionButton: "I-save ang pagtatama",
    cancelEditButton: "kanselahin ang pag-edit",

    // Inline validation messages
    quantityMustBePositive: "Dapat positibong numero ang dami.",
    bothMeterReadingsRequired: "Kailangan ang parehong reading ng meter.",

    // Save-result messages
    recordedMessage: "Naitala ang tubig.",
    recordCorrectedMessage: "Naitama ang tala ng tubig.",

    // Records list — filters
    recordsHeading: "Mga Tala",
    fromLabel: "Mula",
    toLabel: "Hanggang",
    noRecordsMatch: "Walang tumugmang tala ng tubig.",

    // Records table
    dateHeader: "Petsa",
    flockHeader: "Kawan",
    amountHeader: "Dami",
    sourceHeader: "Pinagmulan",
    metersHeader: "Meter",
    noteHeader: "Tala",
    correctButton: "itama",
    loadMoreButton: "mag-load pa",
  },

  // machine-drafted (#182) — pending native review. Task CT2 (B2 catch-up):
  // backfilling tl for the Egg grade catalog admin screen (Task 14, en.ts,
  // batch B2 — the last B2 screen). Keys mirror en.ts grades exactly (no
  // placeholders in this namespace). The Type picker/cell goes through the
  // `enums` gradeTypeLabel helper, and the Active/Inactive status pill
  // through `enums` statusLabel — neither is a key here. Grade NAMES (g.name)
  // are free-form farm data and stay raw, never routed through the catalog.
  // "Sort" kept as an English loanword (same treatment as sales.reference
  // above) — flag for native review.
  grades: {
    title: "Mga Grado ng Itlog",
    loadingTitle: "Mga Grado",

    // Imperative message
    loadGradesFailed: "Hindi na-load ang mga grado. Gumagana ba ang API?",

    intro:
      "Lumalabas ang mga nabebentang grado sa mga picker ng daily entry at "
      + "order. Kapag na-deactivate ang isang grado, aalisin ito sa mga "
      + "picker; hindi maaapektuhan ang umiiral na stock at history.",

    // Buttons
    newGradeButton: "Bagong grado",
    newGradeDialogTitle: "Bagong grado",
    editGradeDialogTitle: "I-edit ang grado",
    addGradeButton: "Magdagdag ng grado",
    editButton: "i-edit",
    deactivateButton: "i-deactivate",
    activateButton: "i-activate",

    // Create-dialog form labels
    nameLabel: "Pangalan *",
    typeLabel: "Uri",
    sortLabel: "Sort",
    saleableLabel: "nabebenta",
    editNameLabel: "Pangalan",

    // Table headers
    nameHeader: "Pangalan",
    typeHeader: "Uri",
    sortHeader: "Sort",
    saleableHeader: "Nabebenta",
    statusHeader: "Katayuan",

    // Saleable column's "yes" badge
    saleableYesBadge: "oo",
  },

  // machine-drafted (#182) — pending native review. Task CT3 (B3 catch-up):
  // backfilling tl for the Feed & inventory screen (Task 16, en.ts, batch
  // B3 — first of four B3 screens). Keys mirror en.ts inventory exactly,
  // including the {{name}}/{{quantity}}/{{unit}}/{{category}}/{{code}}
  // placeholders (no <Trans> tags in this namespace). Category and
  // movement-type displays go through the `enums`
  // inventoryCategoryLabel/inventoryMovementLabel helpers, not a key here —
  // the {{category}} placeholder in notFeedableMessage IS that
  // already-labelled value, per en.ts's own comment. "Lote" used throughout
  // for "lot" (a received inventory batch) — an established loanword
  // already used in dailyEntry's "lote ng itlog", distinct from "kawan"
  // (the word used for bird flocks).
  inventory: {
    title: "Feed at Imbentaryo",
    intro:
      "Tumanggap ng stock bilang pagbili; bawat pagbabago ay napupunta sa "
      + "talaan ng galaw ng item. Susunod ang pagtatala ng paggamit ng feed "
      + "laban sa mga kawan.",

    // Imperative messages
    loadInventoryFailed: "Hindi na-load ang imbentaryo. Gumagana ba ang API?",
    invalidCostError: "Di-wastong halaga.",
    itemCreatedMessage: "Nagawa ang item.",
    loadLedgerFailed: "Hindi na-load ang talaan ng galaw.",
    quantityMustBePositive: "Dapat positibong numero ang dami.",
    purchaseRecordedMessage: "Naitala ang pagbili — natanggap ang stock.",
    usageRecordedMessage:
      "Naitala ang paggamit ng feed — inubos muna ang stock mula sa "
      + "pinakamatandang lote.",
    adjustQuantityRequired:
      "Dapat hindi zero ang dami ng pagsasaayos (negatibo ang nag-aalis ng "
      + "stock).",
    adjustReasonRequired: "Kailangan ng dahilan para sa mga pagtatama.",
    correctionRecordedMessage: "Naitala ang pagtatama sa talaan ng galaw.",

    // Page-head button + New/edit item dialogs
    newItemButton: "Bagong item",
    newItemDialogTitle: "Bagong item ng imbentaryo",
    editItemDialogTitle: "I-edit ang item",
    itemNameLabel: "Pangalan ng item *",
    editItemNameLabel: "Pangalan ng item",
    categoryLabel: "Kategorya",
    unitLabel: "Yunit *",
    editUnitLabel: "Yunit",
    defaultCostLabel: "Default na halaga/yunit",
    addItemButton: "Magdagdag ng item",

    // Item panel (opened item)
    itemPanelHeading: "{{name}} — {{quantity}} {{unit}} available",
    recordPurchaseButton: "Itala ang pagbili",
    recordUsageButton: "Itala ang paggamit",
    correctStockButton: "Itama ang stock",
    notFeedableMessage:
      "Hindi ipinapakain sa mga kawan ang mga item na {{category}} — ang "
      + "paggamit ay para lang sa mga item na Feed, Suplemento, at Aditibo.",
    noFlocksForUsageMessage: "Walang kawan — kailangan ng kawan ang paggamit para pakainin.",
    correctionsNeedAdminMessage: "Kailangan ng admin para sa mga pagtatama ng stock.",
    noLotsMessage: "Wala pang lote — ang mga pagtatama ay para sa isang natanggap na lote.",

    // Record-purchase dialog
    recordPurchaseDialogTitle: "Itala ang pagbili — {{name}}",
    receivedLabel: "Natanggap",
    quantityLabelWithUnit: "Dami ({{unit}})",
    unitCostLabel: "Halaga bawat yunit",
    unitCostWithCurrencyLabel: "Halaga bawat yunit ({{code}})",
    costPlaceholderItemDefault: "default ng item",
    costPlaceholderRequired: "kailangan",
    lotNumberLabel: "Numero ng Lote",
    expiryLabel: "Expiry",
    noteLabel: "Tala",
    recordPurchaseSubmitButton: "Itala ang pagbili",

    // Record-usage dialog
    recordUsageDialogTitle: "Itala ang paggamit — {{name}}",
    flockLabel: "Kawan",
    depletedFlockSuffix: " (naubos — para sa pagtatala ng lumang petsa lang)",
    dateLabel: "Petsa",
    recordUsageSubmitButton: "Itala ang paggamit",

    // Correct-stock dialog
    correctStockDialogTitle: "Itama ang stock — {{name}}",
    lotFieldLabel: "Lote",
    typeLabel: "Uri",
    adjustTypeAdjustmentOption: "Pagsasaayos (±)",
    adjustTypeDiscardOption: "Pagtapon (write-off)",
    adjustQuantityPlaceholderDiscard: "dami na itinapon",
    adjustQuantityPlaceholderCorrection: "± pagtatama",
    reasonLabel: "Dahilan *",
    recordCorrectionButton: "Itala ang pagtatama",

    // Movement ledger table
    ledgerDateHeader: "Petsa",
    ledgerTypeHeader: "Uri",
    ledgerQuantityHeader: "Dami",
    ledgerNoteHeader: "Tala",
    noMovementsMessage: "Wala pang galaw — magtala ng pagbili sa itaas.",
    closeButton: "isara",

    // Items table
    nameHeader: "Pangalan",
    categoryHeader: "Kategorya",
    onHandHeader: "Available",
    defaultCostHeader: "Default na halaga",
    statusHeader: "Katayuan",
    openButton: "buksan",
    editButton: "i-edit",
    deactivateButton: "i-deactivate",
    activateButton: "i-activate",
  },

  // machine-drafted (#182) — pending native review. Task CT3 (B3 catch-up):
  // backfilling tl for the Product catalog + packed-unit conversions screen
  // (Task 17, en.ts, batch B3 — second B3 screen). Keys mirror en.ts
  // products exactly, including the {{count}}/{{code}}/{{unitCode}}
  // placeholders (no <Trans> tags in this namespace). Active/Inactive
  // status on both tables goes through the `enums` statusLabel helper, not
  // a key here. Product/grade names and unitCode are free-form farm data
  // and stay raw. "Catalog" kept as an English loanword (same treatment as
  // sales.reference above) — flag for native review.
  products: {
    title: "Mga Produkto",

    // Imperative messages
    loadCatalogFailed: "Hindi na-load ang catalog. Gumagana ba ang API?",
    enterPriceAsNumber: "Maglagay ng presyo bilang simpleng numero.",
    noDecimalPlaces: "Walang decimal ang currency na ito.",
    atMostDecimals: "Pinakamarami {{count}} decimal para sa currency na ito.",

    intro:
      "Ang ibinebenta ng bukid. Bawat produktong itlog ay naka-map sa isang "
      + "grado ng itlog — kumukuha ang benta ng stock mula sa mga lote ng "
      + "grado na iyon. Kapag na-deactivate, aalisin ang produkto sa mga "
      + "picker; pinapanatili ng history ang pangalan nito.",

    // Page-head button + New/edit product dialogs
    newProductButton: "Bagong produkto",
    newProductDialogTitle: "Bagong produkto",
    editProductDialogTitle: "I-edit ang produkto",

    // Product form labels
    nameLabel: "Pangalan",
    gradeLabel: "Grado",
    pickGradeOption: "Pumili ng grado…",
    soldPerLabel: "Ibinebenta bawat",
    defaultPriceLabel: "Default na presyo",
    defaultPriceWithCurrencyLabel: "Default na presyo ({{code}})",
    priceOptionalPlaceholder: "opsyonal",
    notesLabel: "Mga Tala",
    addProductButton: "Magdagdag ng produkto",

    // Packed-unit (egg-unit-conversion) dialog
    eggsPerUnit: "Itlog kada {{unitCode}}",
    packedUnitDialogTitle: "Yunit ng pakete",
    eggsPerUnitFieldLabel: "Itlog kada yunit",
    activeCheckboxLabel: "aktibo",

    // Products table
    noProductsMessage: "Wala pang produkto.",
    nameHeader: "Pangalan",
    gradeHeader: "Grado",
    soldPerHeader: "Ibinebenta bawat",
    defaultPriceHeader: "Default na presyo",
    statusHeader: "Katayuan",
    editButton: "i-edit",
    deactivateButton: "i-deactivate",
    activateButton: "i-activate",

    // Packed units table
    packedUnitsHeading: "Mga Yunit ng Pakete",
    packedUnitsIntro:
      "Ilang itlog ang laman ng bawat yunit kapag nagbebenta (12, 18, o 30 "
      + "ang isang carton depende sa iyong market — i-set ang sa iyo). Ang "
      + "pagbabago ng yunit ay nakakaapekto lang sa mga susunod na benta; "
      + "pinapanatili ng mga naitalang order ang bilang na ipinagbili sa "
      + "kanila.",
    unitHeader: "Yunit",
    eggsPerUnitHeader: "Itlog kada yunit",
    alwaysOneMessage: "laging 1",
  },

  // machine-drafted (#182) — pending native review. Task CT3 (B3 catch-up):
  // backfilling tl for the Egg stock summary + drill-down screen (Task 18,
  // en.ts, batch B3 — third B3 screen). Keys mirror en.ts stock exactly,
  // including the {{available}}/{{grades}} placeholders (no <Trans> tags in
  // this namespace). The movement ledger's Type cell goes through the
  // `enums` stockMovementLabel helper, not a key here. Grade/lot names and
  // quantity values are free-form farm data and stay raw. "Restricted" and
  // "medication withdrawal" kept as English/Taglish loanwords — no settled
  // short Filipino equivalent in common PH farm-software use; flag for
  // native review.
  stock: {
    title: "Stock",

    // Imperative messages
    loadStockFailed: "Hindi na-load ang stock. Gumagana ba ang API?",
    loadLotsFailed: "Hindi na-load ang mga lote ng grado.",
    loadMovementsFailed: "Hindi na-load ang mga galaw ng lote.",

    noStockMessage: "Wala pang stock — magtala at magsumite ng araw-araw na tala.",

    // By-grade stock table
    gradeHeader: "Grado",
    availableHeader: "Available",
    restrictedHeader: "Restricted",
    lotsButton: "mga lote",
    hideLotsButton: "itago ang mga lote",
    totalAvailableMessage:
      "{{available}} itlog ang available sa {{grades}} grado. Restricted = "
      + "nasa ilalim ng medication withdrawal, hindi puwedeng ibenta.",

    // Lots drill-down (per grade)
    lotsHeading: "Mga Lote",
    noLotsMessage: "Wala pang lote para sa grado na ito.",
    producedOnHeader: "Petsa ng Produksyon",
    producedHeader: "Produksyon",
    historyButton: "history",
    hideHistoryButton: "itago ang history",

    // Movement ledger drill-down (per lot)
    movementLedgerHeading: "Talaan ng Galaw",
    movementLedgerIntro:
      "Bawat pagbabago sa available na itlog ng lote na ito — ang running "
      + "total ay palaging katumbas ng balanse sa itaas.",
    ledgerWhenHeader: "Kailan (UTC)",
    ledgerTypeHeader: "Uri",
    ledgerChangeHeader: "Pagbabago",
    ledgerReasonHeader: "Dahilan",
  },

  // machine-drafted (#182) — pending native review. Task CT3 (B3 catch-up):
  // backfilling tl for the Flock roster + bird ledger screen (Task 19,
  // en.ts, batch B3 — last B3 screen). Keys mirror en.ts flocks exactly,
  // including the {{name}}/{{count}}/{{weeks}} placeholders (no <Trans>
  // tags in this namespace). The bird-ledger Type picker/cell and the
  // flocks table's Status badge go through the `enums`
  // flockMovementLabel/statusLabel helpers, not a key here. Flock
  // name/breed are free-form farm data and stay raw. "deplete"/"archive"
  // translated as "ubusin"/"i-archive" — "ubusin" ties to the already-used
  // "naubos" (depleted) adjective elsewhere in the catalog; "i-archive"
  // matches the i-edit/i-deactivate loanword-verb pattern. Both flagged for
  // native review.
  flocks: {
    title: "Mga Kawan",

    // Imperative messages
    loadFlocksFailed: "Hindi na-load ang mga kawan. Gumagana ba ang API?",
    loadMovementsFailed: "Hindi na-load ang mga galaw.",

    newFlockButton: "Bagong kawan",
    intro:
      "I-deplete kapag wala nang ibon; i-archive para itago ang kawan sa "
      + "mga picker at sa dashboard. Patuloy na nire-resolve ng history ang "
      + "mga pangalan ng mga naka-archive na kawan.",

    // New-flock dialog
    newFlockDialogTitle: "Bagong kawan",
    nameLabel: "Pangalan *",
    breedLabel: "Lahi *",
    placedLabel: "Paglagay",
    birdsLabel: "Mga Ibon",
    addFlockButton: "Magdagdag ng kawan",

    // Edit-flock dialog
    editFlockDialogTitle: "I-edit ang kawan",
    editNameLabel: "I-edit ang pangalan",
    editBreedLabel: "I-edit ang lahi",
    editPlacedLabel: "I-edit ang petsa ng paglagay",
    editCountLabel: "I-edit ang bilang ng ibon",

    // Show-archived toggle
    showArchivedLabel: "ipakita ang {{count}} naka-archive",

    noFlocksMessage: "Wala pang kawan.",

    // Flocks table
    nameHeader: "Pangalan",
    breedHeader: "Lahi",
    placedHeader: "Paglagay",
    ageHeader: "Edad",
    birdsHeader: "Mga Ibon",
    statusHeader: "Katayuan",
    ageWeeksSuffix: "{{weeks}} ling",

    // Row actions
    editButton: "i-edit",
    depleteButton: "ubusin",
    archiveButton: "i-archive",
    reactivateButton: "i-reactivate",
    openLedgerButton: "mga ibon",
    closeLedgerButton: "isara",

    // Deplete/archive confirm dialogs
    depleteConfirmTitle: "Ubusin ang \"{{name}}\"?",
    depleteConfirmBody:
      "Hihinto ang kawan sa pagtanggap ng bagong entry. Gumagana pa rin ang "
      + "pagtatala ng lumang petsa (backfill).",
    depleteConfirmLabel: "Ubusin ang kawan",
    archiveConfirmTitle: "I-archive ang \"{{name}}\"?",
    archiveConfirmBody:
      "Mawawala ito sa mga picker at sa dashboard, at hindi na tatanggap ng bago.",
    archiveConfirmLabel: "I-archive ang kawan",

    // Bird ledger panel
    ledgerHeading: "Talaan ng ibon — {{name}}",
    ledgerIntro: "Ang mga row ng pagkamatay ay galing sa mga naisumiteng daily entry.",
    ledgerIntroAdminNote:
      " Itala ang pagtanggal dito; gumamit ng negatibong pagsasaayos para "
      + "itama ang maling bilang.",
    ledgerIntroWorkerNote: " Kailangan ng admin para magtala ng pagtanggal at pagsasaayos.",
    recordMovementButton: "Itala ang galaw",

    // Record-movement dialog
    recordMovementDialogTitle: "Itala ang galaw ng ibon",
    dateLabel: "Petsa",
    typeLabel: "Uri",
    noteLabel: "Tala",
    recordButton: "Itala",

    noMovementsMessage: "Wala pang galaw — nasa unang bilang pa rin ang kawan.",

    // Movement ledger table
    ledgerDateHeader: "Petsa",
    ledgerTypeHeader: "Uri",
    ledgerBirdsHeader: "Mga Ibon",
    ledgerNoteHeader: "Tala",
  },

  // machine-drafted (#182) — pending native review. Task 25c (B4): new
  // namespace, backfilling tl so Tagalog mode renders translated text on the
  // Settings screen. Keys mirror en.ts settings exactly, including the
  // {{cap}}/{{actualKb}}/{{limitKb}}/{{code}} placeholders and the <strong>
  // tag in logoSquareHint.
  // "Time zone" / "Locale" / "Currency" / "Localization" kept as English
  // loanwords (flag for native review) — no settled short Filipino term is in
  // common use in PH tech UIs for these; same treatment as sales.reference
  // above.
  settings: {
    heading: "Mga setting ng bukid",
    intro:
      "Kung paano tinatawag ng bukid na ito ang sarili nito, at ang locale, "
      + "time zone, at currency na ginagamit nito sa pagtala at pagbasa ng trabaho.",
    loadFailedMessage: "Hindi na-load ang mga setting ng bukid.",

    // Logo panel
    logoSectionHeading: "Logo",
    logoAlt: "Kasalukuyang logo ng bukid",
    logoLoadingMessage: "Naglo-load ang logo…",
    logoLoadFailedMessage: "Hindi na-load ang logo.",
    logoNoneMessage: "Walang naka-set na logo — ipinapakita ng sidebar ang Cluckwork mark.",
    uploadLogoButton: "Mag-upload ng logo",
    replaceLogoButton: "Palitan ang logo",
    removeLogoButton: "Alisin",
    logoRulesHint:
      "PNG, JPEG, o WebP, hanggang {{cap}} at 4096 px kada gilid. Hindi "
      + "tinatanggap ang mga animated na larawan. Na-save ang larawan nang "
      + "na-rewrite, tanggal ang camera at location metadata.",
    logoSquareHint:
      "Gumamit ng <strong>parisukat</strong> na larawan — maliit lang "
      + "lumalabas ang logo sa sidebar, kaya mas maganda ang tingin ng "
      + "simple at maigsing-crop na marka (isang simbolo o iisang letra) "
      + "doon kaysa sa malawak o detalyadong larawan. Mas maganda kung "
      + "transparent ang background sa isang light na disenyo.",
    logoWorkingMessage: "Pinoproseso…",
    logoUpdatedMessage: "Na-update ang logo.",
    logoRemovedMessage: "Naalis ang logo.",
    logoOversizeMessage: "{{actualKb}} KB ang larawang iyon. Ang limitasyon ay {{limitKb}} KB.",
    removeLogoConfirmTitle: "Alisin ang logo ng bukid?",
    removeLogoConfirmBody:
      "Babalik ang sidebar sa Cluckwork mark. Puwede kang mag-upload ulit "
      + "anumang oras.",
    removeLogoConfirmLabel: "Alisin ang logo",

    // Localization form
    localizationSectionHeading: "Localization",
    farmNameLabel: "Pangalan ng bukid",
    timezoneLabel: "Time zone",
    timezoneUnknownWarning:
      "Hindi kilala ng browser na ito ang time zone na iyon, kaya susundin "
      + "ng mga petsa dito ang device sa halip na ang bukid. Pumili ng isa "
      + "sa listahan.",
    localeLabel: "Locale",
    currencyLabel: "Currency",
    currencyLockedNote:
      "Naka-fix ang currency sa {{code}}: may mga halaga na itong naitala "
      + "sa currency na ito. Hindi na muling pinepresyuhan ang naitalang "
      + "pera, kaya kung babaguhin ito, magkakaiba na ang ibig sabihin ng "
      + "bawat naka-save na total.",
    unitSystemLabel: "Sistema ng yunit",
    firstDayOfWeekLabel: "Unang araw ng linggo",
    followLocaleOption: "Sundin ang locale",
    paletteLegend: "Paleta ng bukid",
    paletteHint:
      "Ang accent color para sa lahat sa bukid na ito. Pipiliin pa rin ng "
      + "bawat tao ang sarili nilang light mode o night mode.",
    paletteAubergine: "Talong",
    paletteForest: "Kagubatan",
    paletteSlate: "Slate",
    paletteTerracotta: "Terracotta",
    dateFormatLabel: "Format ng petsa",
    timeFormatLabel: "Format ng oras",
    savingButton: "Sine-save…",
    saveButton: "I-save ang mga setting",
    effectNote:
      "Agad na naaapektuhan ang lahat ng bahagi ng time zone kapag "
      + "na-save na ito. Ang locale, sistema ng yunit, at ang mga override "
      + "sa format ay naitala laban sa bukid at magtatakda kung paano "
      + "ipapakita ang mga halaga, petsa, at sukat kapag dumating na ang "
      + "format na iyon.",
    savedMessage: "Na-save ang mga setting.",

    // Imperative messages
    versionConflictMessage:
      "May ibang taong nagbago ng mga setting na ito habang bukas ang "
      + "screen na ito. I-reload at subukan ulit.",
    saveReadBackFailedMessage:
      "Na-save. Hindi na-basa ulit ng screen na ito ang mga setting — "
      + "i-reload ang page bago mag-save ulit.",
    refreshFailedMessage:
      "Na-save. Hindi na-detect ng ibang bahagi ng app ang pagbabago — "
      + "i-reload ang page para masiguro na naaplay ito sa lahat ng lugar.",
  },

  // machine-drafted (#182) — pending native review. Task 25c (B4): new
  // namespace, backfilling tl so Tagalog mode renders translated text on the
  // Users screen. Keys mirror en.ts users exactly, including the
  // {{label}}/{{email}}/{{role}} placeholders.
  // "flock" translated as "kawan" (the standard Filipino word for a flock of
  // birds) throughout, rather than kept as an English loanword — flag for
  // native review to confirm against local farm-software usage.
  users: {
    heading: "Mga User",
    newUserButton: "Bagong user",
    roleDescription:
      "Itinatala ng mga manggagawa ang trabaho ng araw (opsyonal na "
      + "nakalimita sa mga naka-assign na kawan). Ginagawa rin ng mga "
      + "Manager ang pagtatama, pag-void, at pag-configure. Ang Benta ang "
      + "humahawak sa customer, order, at bayad. Nakikita ng Read-only ang "
      + "stock, history, at reports. Ginagawa ng Admin (may-ari) ang lahat, "
      + "kasama ang pamamahala ng mga user.",

    // Create-user dialog
    emailFieldLabel: "Email *",
    passwordFieldLabel: "Password (min 12 na karakter) *",
    nameFieldLabel: "Pangalan",
    roleFieldLabel: "Tungkulin",
    adminRoleOption: "{{label}} (may-ari)",
    createUserButton: "Gumawa ng user",

    // Users table
    emailColumnHeader: "Email",
    nameColumnHeader: "Pangalan",
    roleColumnHeader: "Tungkulin",
    editButton: "i-edit",
    resetPasswordButton: "password",
    flocksButton: "mga kawan",

    // Flock-access dialog
    flockAccessTitle: "Access sa kawan — {{email}}",
    flockAccessHint:
      "Walang assignment = puwedeng magtala ang manggagawa para sa kahit "
      + "anong kawan. Ang unang assignment ang naglilimita sa kanila sa mga "
      + "nakalistang kawan lang.",
    noAssignmentsMessage: "Walang assignment — access sa buong account.",
    removeAssignmentButton: "alisin",
    assignFlockButton: "Mag-assign ng kawan",
    doneButton: "Tapos na",

    // Edit-user dialog
    editUserTitle: "I-edit ang user — {{email}}",
    clearNameHint: "Iwanang blangko para burahin ang pangalan.",

    // Set-password dialog
    setPasswordTitle: "Itakda ang password — {{email}}",
    passwordDialogHint:
      "Hindi mo kailangan ang kasalukuyan nilang password. Kapag itinakda "
      + "mo ito, ma-si-sign out sila sa lahat ng device — sabihin nang "
      + "direkta sa kanila ang bagong password.",
    newPasswordFieldLabel: "Bagong password (min 12 na karakter) *",
    confirmPasswordFieldLabel: "Kumpirmahin ang bagong password *",
    setPasswordButton: "Itakda ang password",

    // Imperative messages
    createSuccessMessage: "Nagawa ang {{role}} account para sa {{email}}.",
    passwordMismatchMessage: "Hindi magkatugma ang mga password.",
    passwordSetMessage: "Naitakda ang password para sa {{email}}. Na-sign out na sila sa lahat ng lugar.",
    updatedMessage: "Na-update ang {{email}}.",
  },

  // machine-drafted (#182) — pending native review. Task 25c (B4): new
  // namespace, backfilling tl so Tagalog mode renders translated text on the
  // Expenses screen. Keys mirror en.ts expenses exactly, including the
  // {{name}}/{{count}}/{{amount}}/{{code}}/{{date}}/{{description}}
  // placeholders.
  expenses: {
    title: "Mga Gastos",

    // Imperative messages
    expenseRecordedMessage: "Naitala ang gastos.",
    expenseCorrectedMessage: "Naitama ang gastos.",
    conflictRebindMessage:
      "Binago ng ibang tao ang gastos na ito — ipinapakita na ngayon ng "
      + "form ang pinakabagong mga value; i-apply ulit ang iyong pagtatama.",
    categoryCreatedMessage: "Nagawa ang kategorya.",
    categoryDeactivatedMessage: "Na-deactivate ang kategoryang \"{{name}}\".",
    categoryReactivatedMessage: "Na-reactivate ang kategoryang \"{{name}}\".",

    // Amount-parsing validation
    enterValidAmount: "Maglagay ng valid na halaga.",
    noDecimalPlaces: "Walang decimal ang currency na ito.",
    atMostDecimals: "Pinakamarami {{count}} decimal para sa currency na ito.",
    enterAmountGreaterThanZero: "Maglagay ng halagang higit sa zero.",

    // Filters
    monthLabel: "Buwan",
    categoryLabel: "Kategorya",
    allCategoriesOption: "Lahat ng kategorya",
    hideCategoriesButton: "itago ang mga kategorya",
    manageCategoriesButton: "pamahalaan ang mga kategorya",
    monthTotalLabel: "Kabuuan ng buwan: {{amount}}",

    // Category-management panel
    categoriesHeading: "Mga kategorya ng gastos",
    newCategoryButton: "Bagong kategorya",
    newCategoryDialogTitle: "Bagong kategorya ng gastos",
    categoryNameLabel: "Pangalan ng kategorya",
    addCategoryButton: "Magdagdag ng kategorya",
    deactivatedSuffix: " (naka-deactivate)",
    deactivateButton: "i-deactivate",
    reactivateButton: "i-reactivate",
    noCategoriesMessage: "Wala pang kategorya — magdagdag ng isa sa itaas.",

    // Record-expense form
    recordExpenseHeading: "Magtala ng gastos",
    dateLabel: "Petsa",
    pickOption: "— pumili —",
    descriptionLabel: "Deskripsyon",
    amountLabel: "Halaga ({{code}})",
    flockOptionalLabel: "Kawan (opsyonal)",
    noneOption: "— wala —",
    noteOptionalLabel: "Tala (opsyonal)",
    recordExpenseButton: "Itala ang gastos",
    addCategoryFirstMessage: "Magdagdag muna ng kategorya — kailangan nito ng bawat gastos.",

    // Correct-expense dialog
    correctExpenseDialogTitle: "Itama ang gastos",
    correctExpenseDialogTitleWithExpense: "Itama — {{date}}, {{description}}",
    saveCorrectionButton: "I-save ang pagtatama",

    // Expenses table
    noExpensesMessage: "Walang gastos ngayong buwan.",
    dateHeader: "Petsa",
    categoryHeader: "Kategorya",
    descriptionHeader: "Deskripsyon",
    amountHeader: "Halaga",
    flockHeader: "Kawan",
    noteHeader: "Tala",
    correctButton: "itama",
    loadMoreButton: "mag-load pa",
  },

  // machine-drafted (#182) — pending native review. Task 25c (B4): new
  // namespace, backfilling tl so Tagalog mode renders translated text on the
  // Customers screen. Keys mirror en.ts customers exactly (no placeholders
  // in this namespace).
  customers: {
    title: "Mga Customer",
    newCustomerButton: "Bagong customer",

    // Create-customer dialog
    nameFieldLabel: "Pangalan *",
    phoneFieldLabel: "Telepono *",
    emailFieldLabel: "Email",
    addressFieldLabel: "Address",
    noteFieldLabel: "Tala",
    addCustomerButton: "Magdagdag ng customer",

    // Imperative messages
    loadCustomersErrorMessage: "Hindi na-load ang mga customer.",
    loadBalancesErrorMessage: "Hindi na-load ang mga balance ng customer.",

    // Customers table
    noCustomersMessage: "Wala pang customer.",
    nameHeader: "Pangalan",
    phoneHeader: "Telepono",
    emailHeader: "Email",
    addressHeader: "Address",
    noteHeader: "Tala",
    outstandingHeader: "Nakabinbin",
  },

  // machine-drafted (#182) — pending native review. Task 28 (B5): new
  // namespace, backfilling tl so Tagalog mode renders translated text on the
  // History screen. Keys mirror en.ts history exactly, including the
  // {{status}}/{{date}}/{{flock}}/{{total}}/{{mortality}}/{{reason}}/{{time}}
  // placeholders. "Daily entry page" (noEntriesMatch) is kept fully in
  // English, matching the existing sales.addCustomerFirst "(Customers page)"
  // precedent of leaving an unexternalized screen's name untranslated until
  // dailyEntry itself is added to TRANSLATED_NAMESPACES. Status-pill labels
  // (statusVoided/statusAdjusted/statusLocked/statusSubmitted/statusDraft)
  // reuse the same words already chosen for enums.status.* for consistency.
  history: {
    loadingTitle: "Kasaysayan",
    title: "Kasaysayan ng araw-araw na entry",

    intro:
      "Puwedeng i-adjust o i-void dito ang mga naisumite at naka-lock na "
      + "entry — awtomatikong susunod ang stock at ang rekord ng manok; "
      + "hindi na gumagalaw ang mga itlog na nabenta na. Palaging "
      + "kailangan ng dahilan.",

    concurrentConflictMessage:
      "Kababago lang ng entry na ito sa ibang lugar — na-reload na ang listahan; subukan ulit.",
    loadFlocksGradesFailed: "Hindi na-load ang mga kawan/grado.",
    loadEntriesFailed: "Hindi na-load ang mga entry.",
    conflictRebindMessage:
      "Binago ng ibang tao ang entry na ito — ipinapakita na ngayon ng "
      + "form ang pinakabagong mga value; i-apply ulit ang iyong pagtatama.",
    nothingToAdjustMessage: "{{status}} na ngayon ang entry na ito — wala nang puwedeng i-adjust.",
    conflictReloadFailedMessage:
      "Binago ng ibang tao ang entry na ito at hindi na-reload ang "
      + "listahan — i-reload ang page bago subukan ulit.",
    exceedsSellableMessage:
      "Hindi puwedeng lumampas ang mga na-grade na dami sa kabuuang itlog na bawas ang basag/marumi/tinapon.",
    entryAdjustedMessage: "Na-adjust ang entry — na-update ang stock at ang rekord ng manok para tumugma.",
    adjustReloadFailedMessage: "Na-save ang pag-adjust, pero hindi na-reload ang listahan — i-refresh ang page.",
    voidConfirmTitle: "I-void ang entry ng {{date}} para sa {{flock}}?",
    voidConfirmBody:
      "Mawawalan ng laman ang mga lote ng itlog nito at mababaliktad ang "
      + "mga namatay dito. Mananatiling Na-void ang entry. Tatanggihan "
      + "kung nabenta na ang alinman sa mga itlog nito.",
    voidConfirmLabel: "I-void ang entry",
    entryVoidedMessage: "Na-void ang entry — nawalan ng laman ang mga lote ng itlog nito at nabaliktad ang mga namatay dito.",
    voidReloadFailedMessage: "Na-save ang pag-void, pero hindi na-reload ang listahan — i-refresh ang page.",
    voidConflictMessage: "Binago ng ibang tao ang entry na ito — na-reload na ang listahan; subukan ulit.",
    voidConflictReloadFailedMessage:
      "Binago ng ibang tao ang entry na ito at hindi na-reload ang "
      + "listahan — i-reload ang page.",
    loadMoreFailedMessage: "Hindi na-load ang karagdagan.",

    // Filters
    flockLabel: "Kawan",
    allFlocksOption: "Lahat ng kawan",
    fromLabel: "Mula",
    toLabel: "Hanggang",

    // Adjust dialog
    adjustDialogTitle: "I-adjust ang entry",
    adjustDialogTitleWithEntry: "I-adjust — {{date}}, {{flock}}",
    previouslyAdjusted:
      "Na-adjust na dati (kabuuan {{total}}, mortalidad {{mortality}} — \"{{reason}}\").",
    totalEggsLabel: "Kabuuang itlog",
    crackedLabel: "Basag",
    dirtyLabel: "Marumi",
    discardedLabel: "Tinapon",
    deathsLabel: "Namatay",
    inactiveGradeSuffix: " (hindi aktibo)",
    reasonLabel: "Dahilan *",
    saveAdjustmentButton: "I-save ang pag-adjust",

    noEntriesMatch: "Walang tugmang entry — magtala ng isa sa Daily entry page.",

    // Entries table
    dateHeader: "Petsa",
    flockHeader: "Kawan",
    statusHeader: "Katayuan",
    totalHeader: "Kabuuan",
    // NOTE (flag for native review): "cr/di/ds" abbreviates the English
    // cracked/dirty/discarded; re-abbreviated here to the initials of the
    // Filipino words (basag/marumi/tapon → b/m/t) — confirm this reads
    // clearly as a table-header abbreviation.
    lossesHeader: "Nasira (b/m/t)",
    mortalityHeader: "Mortalidad",
    gradedHeader: "Na-grade",
    editButton: "i-edit",
    adjustButton: "i-adjust",
    voidButton: "i-void",
    loadMoreButton: "mag-load pa",

    // Entry-status pills
    statusVoided: "Na-void",
    statusAdjusted: "Na-adjust",
    statusLocked: "Naka-lock",
    lockedAt: "Naka-lock {{time}}",
    statusSubmitted: "Naisumite",
    statusDraft: "Draft",
  },

  // machine-drafted (#182) — pending native review. Task 28 (B5): new
  // namespace, backfilling tl so Tagalog mode renders translated text on the
  // Reports screen. Keys mirror en.ts reports exactly, including the
  // {{count}}/{{revenue}}/{{paid}}/{{outstanding}}/{{total}}/{{expenses}}/
  // {{profit}} placeholders and the <strong> tag in profitLine. "Hen-days" /
  // "Hen-day %" kept as English loanwords (technical poultry-industry
  // metric, no settled short Filipino term) — flag for native review, same
  // treatment as settings.timezoneLabel above. "cost-of-goods" /
  // "inventory valuation" in profitFootnote kept partially in English for
  // the same reason.
  reports: {
    title: "Mga Ulat",
    fromLabel: "Mula",
    toLabel: "Hanggang",

    productionHeading: "Produksyon",
    dateHeader: "Petsa",
    eggsHeader: "Itlog",
    lossesHeader: "Nasira (b/m/t)",
    sellableHeader: "Maibebenta",
    deathsHeader: "Namatay",
    henDaysHeader: "Hen-days",
    henDayPctHeader: "Hen-day %",
    periodRowLabel: "Panahon",
    gradeTotalsLabel: "Ayon sa grado:",

    moneyHeading: "Pera",
    salesRowLabel: "Benta",
    salesSummary:
      "{{count}} kumpirmadong order — kita {{revenue}}, nabayaran {{paid}}, nakabinbin {{outstanding}}",
    salesVoidedSuffix: " ({{count}} na-void)",
    expensesRowLabel: "Gastos",
    expensesNone: "wala pang naitala",
    expensesTotalSuffix: " — kabuuan {{total}}",
    profitRowLabel: "Kita (payak)",
    profitLine: "kita {{revenue}} − gastos {{expenses}} = <strong>{{profit}}</strong>",
    profitFootnote:
      "Ang \"payak\" na kita ay ang nakumpirmang kita bawas ang mga "
      + "naitalang gastos — walang cost-of-goods o valuation ng imbentaryo.",
  },

  // machine-drafted (#182) — pending native review. Task 29 (B5): new
  // namespace, backfilling tl so Tagalog mode renders translated text on the
  // Audit screen. Keys mirror en.ts audit exactly (no placeholders in this
  // namespace — the action/entity table cells route through the already-
  // translated enums:auditAction.*/entityType.* labels, not this
  // namespace). "corrective"/"destructive"/"configuration" kept as English
  // loanword adjectives in intro (matching the loanword-heavy register
  // elsewhere in this file, e.g. sales.confirmOrderButton); "Audit Log"
  // kept as a loanword heading, same treatment as settings.localeLabel.
  audit: {
    heading: "Audit Log",
    intro:
      "Bawat corrective, destructive, o configuration na pagbabago — sino "
      + "ang gumawa, kailan, at bakit. Isinusulat ang mga row kasabay ng "
      + "pagbabago mismo at hindi na ito ine-edit pa.",
    actionFilterLabel: "Aksyon",
    allActionsOption: "Lahat ng aksyon",
    whenHeader: "Kailan (UTC)",
    whoHeader: "Sino",
    actionHeader: "Aksyon",
    entityHeader: "Entidad",
    reasonHeader: "Dahilan",
    emptyMessage: "Wala pang audit event.",
    loadMoreButton: "mag-load pa",
  },

  // machine-drafted (#182) — pending native review. Task 30 (B5): new
  // namespace, backfilling tl so Tagalog mode renders translated text on the
  // Export screen. Keys mirror en.ts export exactly (no placeholders in
  // this namespace). The "dataset.<slug>" keys are DISPLAY labels for the
  // dataset picker (the wire value stays the raw slug) — translated to
  // natural Filipino per the task brief.
  export: {
    heading: "Export",
    intro:
      "I-download ang datos ng iyong account bilang mga CSV file — isang "
      + "manual na backup na puwede mong itago kahit saan. Ang mga "
      + "halagang pera ay ine-export sa minor units (sentimo) kasama ang "
      + "currency nito, eksakto gaya ng pagkaka-save.",

    fullBackupHeading: "Buong backup",
    fullBackupButton: "I-download ang buong backup (zip)",
    fullBackupHint: "Isang zip na may lahat ng dataset sa ibaba kasama ang manifest ng bilang ng row.",
    preparingButton: "Naghahanda…",

    singleDatasetsHeading: "Mga indibidwal na dataset",

    "dataset.flocks": "Mga kawan",
    "dataset.bird-movements": "Mga paggalaw ng manok",
    "dataset.daily-entries": "Mga araw-araw na entry",
    "dataset.daily-entry-grades": "Mga grado ng araw-araw na entry",
    "dataset.egg-grades": "Mga grado ng itlog",
    "dataset.egg-lots": "Mga lote ng itlog",
    "dataset.customers": "Mga customer",
    "dataset.sales-orders": "Mga order ng benta",
    "dataset.sales-order-items": "Mga item ng order ng benta",
    "dataset.sales-order-allocations": "Mga allocation ng order ng benta",
    "dataset.payments": "Mga bayad",
    "dataset.inventory-items": "Mga item sa imbentaryo",
    "dataset.inventory-lots": "Mga lote ng imbentaryo",
    "dataset.inventory-movements": "Mga paggalaw ng imbentaryo",
    "dataset.feed-usages": "Mga paggamit ng feed",
    "dataset.water-usages": "Mga paggamit ng tubig",
    "dataset.expense-categories": "Mga kategorya ng gastos",
    "dataset.expenses": "Mga gastos",
    "dataset.egg-inventory-movements": "Mga paggalaw ng imbentaryo ng itlog",
    "dataset.audit-events": "Mga audit event",
  },

  // machine-drafted (#182) — pending native review. Task 25c (B4): new
  // namespace, backfilling tl for the closed-vocabulary enum labels
  // (status/role/waterSource/waterUnit/gradeType/inventoryCategory/
  // inventoryMovement/flockMovement/stockMovement/unitSystem/weekday)
  // consumed through enums.ts, so Settings' unit-system/weekday pickers and
  // Users' role values render translated text too. Keys mirror en.ts enums
  // exactly (flat "family.RawValue" strings, keySeparator:false — see
  // en.ts's enums header comment). waterUnit.L/waterUnit.gal are left as the
  // literal unit symbols (unchanged across en/es/tl), matching en's own
  // comment.
  // role.Admin/role.Manager/role.ReadOnly and a handful of inventory/stock
  // terms (gradeType.Custom, inventoryCategory.Packaging, stockMovement.
  // Reconciliation) are kept as English loanwords — common register in PH
  // farm/business software, same treatment as sales.methodCash/methodCard
  // above; flagged for native review alongside role.Worker ("Manggagawa")
  // and role.Sales ("Benta", matching sales.title) which WERE translated.
  enums: {
    // status
    "status.Active": "Aktibo",
    "status.Inactive": "Hindi Aktibo",
    "status.Draft": "Draft",
    "status.Submitted": "Naisumite",
    "status.Locked": "Naka-lock",
    "status.ManagerAdjusted": "Na-adjust",
    "status.Voided": "Na-void",
    "status.Confirmed": "Kumpirmado",
    "status.Shipped": "Ipinadala",
    "status.Invoiced": "Na-invoice",
    "status.Cancelled": "Kinansela",
    "status.Depleted": "Naubos",
    "status.Archived": "Naka-archive",

    // role
    "role.Worker": "Manggagawa",
    "role.Admin": "Admin",
    "role.Manager": "Manager",
    "role.Sales": "Benta",
    "role.ReadOnly": "Read-only",

    // water source
    "waterSource.Well": "Poso",
    "waterSource.Municipal": "Munisipal",
    "waterSource.Tank": "Tangke",
    "waterSource.Other": "Iba pa",

    // water unit — symbols, unchanged
    "waterUnit.L": "L",
    "waterUnit.gal": "gal",

    // grade type
    "gradeType.Size": "Sukat",
    "gradeType.Quality": "Kalidad",
    "gradeType.Custom": "Custom",

    // inventory category
    "inventoryCategory.Feed": "Feed",
    "inventoryCategory.Supplement": "Suplemento",
    "inventoryCategory.Additive": "Aditibo",
    "inventoryCategory.Medication": "Gamot",
    "inventoryCategory.Vaccine": "Bakuna",
    "inventoryCategory.Packaging": "Packaging",
    "inventoryCategory.Bedding": "Higaan",
    "inventoryCategory.Sanitation": "Kalinisan",
    "inventoryCategory.EquipmentPart": "Bahagi ng kagamitan",
    "inventoryCategory.Other": "Iba pa",

    // inventory movement type
    "inventoryMovement.Purchase": "Pagbili",
    "inventoryMovement.Usage": "Paggamit",
    "inventoryMovement.Adjustment": "Pagsasaayos",
    "inventoryMovement.Discard": "Pagtapon",

    // flock (bird) movement type
    "flockMovement.Mortality": "Pagkamatay",
    "flockMovement.Cull": "Pagtanggal",
    "flockMovement.Adjustment": "Pagsasaayos",

    // egg stock movement type
    "stockMovement.Production": "Produksyon",
    "stockMovement.Sale": "Benta",
    "stockMovement.Adjustment": "Pagsasaayos",
    "stockMovement.Discard": "Pagtapon",
    "stockMovement.InternalUse": "Panloob na paggamit",
    "stockMovement.Transfer": "Paglipat",
    "stockMovement.Reconciliation": "Reconciliation",
    "stockMovement.Void": "Na-void",

    // unit system
    "unitSystem.Metric": "Metriko",
    "unitSystem.Imperial": "Imperyal",

    // weekday
    "weekday.Sunday": "Linggo",
    "weekday.Monday": "Lunes",
    "weekday.Tuesday": "Martes",
    "weekday.Wednesday": "Miyerkules",
    "weekday.Thursday": "Huwebes",
    "weekday.Friday": "Biyernes",
    "weekday.Saturday": "Sabado",

    // machine-drafted (#182) — pending native review. Task 29 (B5): audit
    // action + entity type labels for AuditPage. Keys mirror en.ts exactly
    // (flat "auditAction.Entity.Verb" / "entityType.Value" strings,
    // keySeparator:false — see en.ts's auditAction header comment).
    // "Order", "User", "Account", "item", and "conversion" kept as English
    // loanwords, matching the sales/users precedent above; "kawan" (flock),
    // "gastos" (expense), "bayad" (payment), "produkto" (product), "itlog"
    // (egg), and "imbentaryo" (inventory) reuse the vocabulary already
    // established in the sales/users/expenses/customers namespaces.
    "auditAction.DailyEntry.Adjust": "Na-adjust ang araw-araw na entry",
    "auditAction.DailyEntry.Void": "Na-void ang araw-araw na entry",
    "auditAction.SalesOrder.Void": "Na-void ang order ng benta",
    "auditAction.Payment.Void": "Na-void ang bayad",
    "auditAction.Expense.Adjust": "Na-adjust ang gastos",
    "auditAction.ExpenseCategory.Update": "Na-update ang kategorya ng gastos",
    "auditAction.InventoryItem.Adjust": "Na-adjust ang item sa imbentaryo",
    "auditAction.WaterUsage.Correct": "Naitama ang paggamit ng tubig",
    "auditAction.Flock.BirdMovement": "Naitala ang paggalaw ng manok",
    "auditAction.Flock.Update": "Na-update ang kawan",
    "auditAction.Flock.Deplete": "Naubos ang kawan",
    "auditAction.Flock.Archive": "Na-archive ang kawan",
    "auditAction.Flock.Reactivate": "Na-reactivate ang kawan",
    "auditAction.EggGrade.Update": "Na-update ang grado ng itlog",
    "auditAction.EggGrade.Activate": "Na-activate ang grado ng itlog",
    "auditAction.EggGrade.Deactivate": "Na-deactivate ang grado ng itlog",
    "auditAction.User.Create": "Nagawa ang user",
    "auditAction.User.Update": "Na-update ang user",
    "auditAction.User.PasswordSet": "Naitakda ang password",
    "auditAction.User.PasswordChanged": "Napalitan ang password",
    "auditAction.User.FlockAssign": "Na-assign ang kawan sa user",
    "auditAction.User.FlockUnassign": "Na-unassign ang kawan mula sa user",
    "auditAction.Account.Export": "Na-export ang datos",
    "auditAction.Product.Create": "Nagawa ang produkto",
    "auditAction.Product.Update": "Na-update ang produkto",
    "auditAction.Product.Activate": "Na-activate ang produkto",
    "auditAction.Product.Deactivate": "Na-deactivate ang produkto",
    "auditAction.EggUnitConversion.Update": "Na-update ang conversion ng yunit ng itlog",

    "entityType.Account": "Account",
    "entityType.DailyEntry": "Araw-araw na Entry",
    "entityType.EggGrade": "Grado ng Itlog",
    "entityType.EggUnitConversion": "Conversion ng Yunit ng Itlog",
    "entityType.Expense": "Gastos",
    "entityType.ExpenseCategory": "Kategorya ng Gastos",
    "entityType.Flock": "Kawan",
    "entityType.InventoryItem": "Item sa Imbentaryo",
    "entityType.Payment": "Bayad",
    "entityType.Product": "Produkto",
    "entityType.SalesOrder": "Order ng Benta",
    "entityType.User": "User",
    "entityType.WaterUsage": "Paggamit ng Tubig",
  },
} as const;
