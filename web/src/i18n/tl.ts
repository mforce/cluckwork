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
    whatDoesTermMean: "Ano ang ibig sabihin ng “{{term}}”?",
    "recordHistory.createdBy": "Ginawa ni {{email}} noong {{at}}",
    "recordHistory.lastChangedBy": "Huling binago ni {{email}} noong {{at}}",
    "recordHistory.submittedAt": "Naisumite noong {{at}}",
    "recordHistory.confirmedAt": "Nakumpirma noong {{at}}",
    "recordHistory.viewHistoryLink": "Kasaysayan ng audit",
    "recordHistory.viewAdjustmentHistoryLink": "Kasaysayan ng pagsasaayos",
    recordHistoryHeader: "Kasaysayan",
    cancel: "Kanselahin",
    save: "I-save",
    close: "Isara",
    delete: "Burahin",
    edit: "I-edit",
    add: "Idagdag",
    confirm: "Kumpirmahin",
    loading: "Naglo-load…",
    working: "Gumagana…",
    workingHint:
      "Kapag may umiikot na indicator ang isang button, gumagana pa ang pag-save — hindi maitatala nang dalawang beses kahit pindutin itong muli.",
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
    // machine-drafted (#532) — pending native review.
    farmCode: "Code ng bukid",
    recentFarms: "Mga kamakailang bukid",
    // #587 (machine-drafted, pending native review) — the per-entry destructive control and its confirmation dialog.
    forgetFarm: "Kalimutan ang {{farmCode}}",
    forgetFarmTitle: "Gusto mong kalimutan ang {{farmCode}}?",
    forgetFarmBody:
      "Aalis nito ang {{farmCode}} sa mga kamakailang bukid sa device na ito. Hindi binabago nito ang iyong account o ibang setting ng device.",
    forgetFarmConfirm: "Kalimutan ang bukid",
    farmFromLink: "Nagsa-sign in sa bukid: {{farmCode}}",
    unknownFarmCode: "Hindi nakikilala ang code ng bukid na iyon. Suriin ito at subukang muli.",
    farmSuspended: "Suspendido ang bukid na ito. Makipag-ugnayan sa iyong administrator.",
    email: "Email",
    password: "Password",
    signIn: "Mag-sign in",
    signingIn: "Nagsa-sign in…",
    invalidCredentials: "Mali ang email o password.",
    credentialsSuperseded: "Nagbago ang iyong mga kredensyal. Mag-sign in muli.",
    accountDisabled: "Na-disable ang iyong account.",
    // #532 — per-farm refresh cookie: several farms hold sessions in this
    // browser. Machine-drafted (#182) — pending native review.
    farmSelectionRequired:
      "May mga session para sa ilang bukid sa browser na ito. Pumili ng bukid at mag-sign in.",
    tooManyAttempts:
      "Sobra na ang subok sa pag-sign in. Maghintay ng ilang minuto at subukan ulit.",
    apiDown: "Hindi makapag-sign in. Gumagana ba ang API?",
    // machine-drafted (#182) — pending native review.
    credentialsTooLong: "Sobrang haba niyan — tingnan ang iyong email at password.",
    noAdminYet:
      "Wala pang administrator. Hindi pa natatapos ang paunang setup ng farm "
      + "na ito, kaya wala pang administrator account na maaaring gamitin sa "
      + "pag-sign in.",
    noAdminYetHint:
      "Hilingin sa kung sino ang nag-set up ng server na ito na gawin ang unang "
      + "administrator. Nasa README ng proyekto ang mga hakbang sa setup.",
    // machine-drafted (#283) — pending native review.
    setPasswordHeading: "Itakda ang iyong password",
    setPasswordHint:
      "Ito ang iyong unang pag-sign in. Magtakda ng bagong password para "
      + "magpatuloy — hindi na gagana ang pansamantalang password pagkatapos nito.",
    temporaryPasswordLabel: "Pansamantalang password",
    setPasswordNewLabel: "Bagong password (min {{min}} karakter)",
    setPasswordConfirmLabel: "Kumpirmahin ang bagong password",
    setPasswordButton: "Itakda ang password",
    setPasswordSubmitting: "Itinatakda ang password…",
    setPasswordSignOut: "Mag-sign out",
    setPasswordMismatchError: "Hindi magkatugma ang mga bagong password.",
    setPasswordTooShortError: "Dapat hindi bababa sa {{min}} karakter ang bagong password.",
  },
  account: {
    preferences: "Mga Kagustuhan",
    language: "Wika",
    languageHint: "Ang wikang gagamitin sa interface, para lang sa iyo.",
    stepperUnit: "Yunit ng pagbilang sa Daily Entry",
    stepperUnitHint:
      "Kung magkano ang ibinibilang ng mga +/− na button ng Daily Entry, para lang sa iyo — "
      + "pumili ng pack unit tulad ng Tray para magbilang bawat tray sa halip na bawat itlog, "
      + "o sundin ang default ng bukid.",
    stepperUnitFarmDefaultOption: "Default ng bukid ({{unit}})",
    stepperUnitSaveFailed: "Hindi na-save — hindi nabago ang iyong yunit ng pagbilang.",

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
    "EggLot.AssignedFlocksInsufficientStock":
      "Wala pang sapat na stock ang mga kawan na nakatalaga sa iyo para sa "
      + "bentang ito. Maaaring paganahin ng isang Owner o Manager ang pagbenta "
      + "mula sa ibang kawan sa Mga setting ng bukid.",
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
    feed: "Pakain",
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
    versionLabel: "v{{version}}",
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
    increaseByLabel: "Dagdagan ang {{label}} ng {{step}}",
    decreaseByLabel: "Bawasan ang {{label}} ng {{step}}",
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
  namedEntityPicker: {
    loadMore: "Mag-load ng higit pa",
    loading: "Naglo-load…",
    noResults: "Walang natagpuang tugma",
    clear: "Burahin",
    // #512 US3 — pag-recover at mga announcement ng estado (picker-ui.md).
    retry: "Subukan ulit",
    unavailable: "Hindi available",
    unavailableExplanation: "Hindi na available ang record na ito.",
    searchFailed: "Hindi nakamag-search",
    loadMoreFailed: "Hindi nakama-load ng higit pa",
    results: "{{count}} resulta",
  },
  splash: {
    continue: "Magpatuloy",
    bannerAlt: "Banner ng {{farmName}}",
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
    // #445
    quantityWithUnit: "Dami ({{unit}})",
    // The _one strings equal the base ON PURPOSE: Tagalog's CLDR "one"
    // category is broad (1, 2, 3, 5, 12, 30, …), not "exactly one", and
    // Tagalog nouns don't inflect for number anyway — the keys exist only to
    // satisfy catalog parity with en's genuinely singular forms.
    equalsEggs: "= {{count}} na itlog",
    equalsEggs_one: "= {{count}} na itlog",
    productOptionWithUnit: "{{name}} ({{count}} na itlog/{{unit}})",
    productOptionWithUnit_one: "{{name}} ({{count}} na itlog/{{unit}})",
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

    // Status-filter options — status labels now come from enums:status (#182).
    allOption: "Lahat",

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
    // #512 (T039)
    pickCustomerOption: "— pumili ng customer —",
    rowCustomerUnavailable: "Hindi na available ang customer na ito.",
    filterCustomerUnavailable: "Hindi na available ang customer na ito.",
    // #612
    farmWideAllocationNotice:
      "Pinapayagan ng setting na ito ng bukid na kumuha ng stock ang iyong "
      + "mga kumpirmasyon ng benta mula sa labas ng mga kawan na nakatalaga "
      + "sa iyo.",
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
    // machine-drafted (#182) — pending native review.
    quantityMustBeWholeNumber: "Dapat buong bilang ang dami.",
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
    // #512 — pisked trigger ng FlockPicker bago ang default value.
    selectFlockOption: "Pumili ng kawan",
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
    // #446 — ang _one ay katumbas ng base nang sadya (malawak ang CLDR "one"
    // ng Tagalog at hindi nagbabago ang anyo ng pangngalan).
    daySupportFeed: "Pakain: {{count}} na tala (tantiya: {{cost}})",
    daySupportFeed_one: "Pakain: {{count}} na tala (tantiya: {{cost}})",
    // Cost dropped when the day's rows span currencies — never a blended sum.
    daySupportFeedNoCost: "Pakain: {{count}} na tala",
    daySupportFeedNoCost_one: "Pakain: {{count}} na tala",
    daySupportFeedNone: "Pakain: 0 tala",
    daySupportWater: "Tubig: {{count}} na tala",
    daySupportWater_one: "Tubig: {{count}} na tala",
    daySupportWaterNone: "Tubig: 0 tala",
    stepperUnitCaption: "Nagbibilang bawat {{unit}} — bawat tap ng − / + ay gumagalaw ng {{count}} itlog. Ang pag-type ay naglalagay pa rin ng eksaktong numero.",
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

    // Katayuan ng pagtatala (#654)
    todayPanelTitle: "Ngayon",
    noFlocksMessage: "Wala pang kawan — gumawa ng isa sa page na Araw-araw na Tala.",
    noEntryBadge: "walang tala",
    todayEggsTotal: "{{total}} itlog ngayon",
    tileLinkLabel: "{{flock}}: buksan ang tala ngayong araw",
    moreFlocks_one: "{{total}} pang kawan",
    moreFlocks_other: "{{total}} pang kawan",

    // Huling 14 araw (#654)
    trendPanelTitle: "Huling 14 araw",
    sparklineLabel: "Itlog bawat araw, huling 14 araw: pinakamababa {{min}}, pinakamataas {{max}}, kahapon {{last}}",
    henDayCaption: "Hen-day {{pct}} · {{delta}} kumpara sa nakaraang 7 araw",
    henDayDeltaUp: "+{{delta}} pts",
    henDayDeltaDown: "−{{delta}} pts",

    // Stock (#654)
    stockPanelTitle: "Stock",
    noStockMessage: "Wala pang stock — magtala at magsumite ng araw-araw na tala.",
    eggsAvailableMessage: "{{total}} itlog ang available.",
    stockCaptionRestricted: "{{restricted}} restricted",

    // Mga kamakailang benta (nakatago para sa ReadOnly/Denied, #127)
    salesPanelTitle: "Mga Kamakailang Benta",
    noOrdersMessage: "Wala pang order.",
    rowCustomerUnavailable: "Hindi na available ang customer na ito.",
  },

  // machine-drafted (#182) — pending native review. Task CT2 (B2 catch-up):
  // backfilling tl for the Water capture + correction screen (Task 13, en.ts,
  // batch B2). Keys mirror en.ts water exactly, including the {{unit}}
  // placeholder (no <Trans> tags in this namespace). Source/Unit picker
  // values go through the `enums` waterSourceLabel/waterUnitLabel helpers,
  // not a key here.
  // #446 — machine-drafted, pending native review (translate-now policy).
  feed: {
    title: "Pakain",
    loadFailed: "Hindi ma-load ang mga kawan at mga item ng pakain. Gumagana ba ang API?",
    loadRecordsFailed: "Hindi ma-load ang mga tala ng pakain.",
    intro:
      "Itala kung ano ang ipinakain sa bawat kawan. Ang stock ay kinukuha muna "
      + "mula sa pinakalumang mga binili at ang tantiyang gastos ay mula sa mga lot na iyon.",
    flockLabel: "Kawan",
    // #512 — pisked trigger ng FlockPicker bago ang default value.
    selectFlockOption: "Pumili ng kawan",
    depletedFlockSuffix: " — ubos na, backfill lamang",
    itemLabel: "Item",
    itemOption: "{{name}} ({{onHand}} {{unit}} na natitira)",
    dateLabel: "Petsa",
    quantityLabel: "Dami",
    quantityLabelWithUnit: "Dami ({{unit}})",
    noteLabel: "Tala",
    recordFeedButton: "Itala ang pakain",
    quantityMustBePositive: "Ang dami ay dapat positibong numero.",
    recordedMessage: "Naitala ang pakain.",
    correctionsHint:
      "Ang maling naitalang pagpapakain ay itinutuwid sa pamamagitan ng Inventory "
      + "adjustment sa apektadong lot — hindi kailanman ine-edit ang mga tala ng pakain.",
    filterFlockLabel: "Salain ayon sa kawan",
    // #512 T038 — hindi na maibasa ang id ng fila; nananatili ang EXACT na
    // filter (hindi papalitan) at ang retry ay naga-redo lang ng GET.
    filterFlockUnavailable:
      "Hindi na available ang kawan na nakarecord sa mga rekord na ito.",
    rowFlockUnavailable: "Hindi na available ang kawan na ito.",
    inactiveItemSuffix: " — hindi aktibo, inuubos ang natitirang stock",
    inactiveEmptyItemSuffix: " — hindi aktibo, wala nang stock",
    recordsHeading: "Mga tala",
    fromLabel: "Mula",
    toLabel: "Hanggang",
    noRecordsMatch: "Walang tumutugmang tala ng pakain.",
    dateHeader: "Petsa",
    flockHeader: "Kawan",
    itemHeader: "Item",
    amountHeader: "Dami",
    estimatedCostHeader: "Tantiyang gastos",
    noteHeader: "Tala",
    loadMoreButton: "mag-load pa",
  },
  water: {
    title: "Tubig",

    // Imperative messages
    loadFlocksFailed: "Hindi na-load ang mga kawan. Gumagana ba ang API?",
    loadRecordsFailed: "Hindi na-load ang mga tala ng tubig.",
    concurrentEditError:
      "Kababago lang ng record na ito sa ibang lugar — i-reload ang "
      + "listahan at subukan ulit.",

    intro:
      "Itala kung ano ang ininom ng bawat kawan — direktang dami, o mga "
      + "reading ng meter (ang dami ay ang delta ng meter). Puwedeng itama "
      + "ang mga tala pagkatapos; naka-fix ang kawan at petsa.",

    // Capture form labels
    flockLabel: "Kawan",
    selectFlockOption: "Pumili ng kawan",
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
    filterFlockLabel: "Salain ayon sa kawan",
    filterFlockUnavailable:
      "Ang kalahi na kawan sa mga tala na ito ay hindi na available.",
    rowFlockUnavailable: "Hindi na available ang kawan na ito.",
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
    loadLotsFailed: "Hindi na-load ang mga lote ng item.",
    quantityMustBePositive: "Dapat positibong numero ang dami.",
    purchaseRecordedMessage: "Naitala ang pagbili — natanggap ang stock.",
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
    recordUsageLink: "Itala ang paggamit sa pahina ng Pakain",
    correctStockButton: "Itama ang stock",
    notFeedableMessage:
      "Hindi ipinapakain sa mga kawan ang mga item na {{category}} — ang "
      + "paggamit ay para lang sa mga item na Feed, Suplemento, at Aditibo.",
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
    flockLabel: "Kawan",
    depletedFlockSuffix: " (naubos — para sa pagtatala ng lumang petsa lang)",
    dateLabel: "Petsa",

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
    loadMoreButton: "mag-load pa",
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
    // #465 — paging + saklaw ng petsa sa talahanayan ng mga lote.
    fromLabel: "Mula",
    toLabel: "Hanggang",
    loadMoreButton: "mag-load pa",

    // Movement ledger drill-down (per lot)
    movementLedgerHeading: "Talaan ng Galaw",
    movementLedgerIntro:
      "Bawat pagbabago sa available na itlog ng lote na ito — ang running "
      + "total ay palaging katumbas ng balanse sa itaas.",
    ledgerWhenHeader: "Kailan (UTC)",
    ledgerTypeHeader: "Uri",
    ledgerChangeHeader: "Pagbabago",
    ledgerReasonHeader: "Dahilan",

    // #406 — write-off / reconciliation kada lote (Owner/Manager lamang).
    writeOffButton: "i-write off",
    writeOffNeedsAdminMessage: "Ang mga pagwawasto ng stock ay nangangailangan ng Owner o Manager.",
    writeOffDialogTitle: "Iwasto ang stock — lote ng {{date}}",
    writeOffTypeLabel: "Uri",
    writeOffDirectionLabel: "Direksyon",
    writeOffDirectionRemoveOption: "Bawasan ang itlog (nawala o mas kaunti sa naitala)",
    writeOffDirectionAddOption: "Ibalik ang itlog (mas marami ang nabilang)",
    writeOffQuantityLabel: "Itlog",
    writeOffReasonLabel: "Dahilan",
    writeOffPreviewMessage: "{{current}} → {{result}} ang available",
    writeOffSubmitButton: "Itala ang pagwawasto",
    writeOffRecordedMessage: "Naitala ang pagwawasto — {{available}} na ang available.",
    writeOffQuantityRequired: "Ilagay kung ilang itlog.",
    writeOffReasonRequired: "Kailangan ng dahilan ang mga pagwawasto.",
    writeOffOutOfRangeMessage:
      "Ang resulta ay dapat manatili sa pagitan ng 0 at ng {{produced}} na ginawa ng loteng ito. "
      + "Ang bilang na lampas sa produksyon ay pagsasaayos ng araw-araw na entry.",
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
    loadMoreButton: "mag-load pa",
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
      "Maliit lang lumalabas ang logo sa sidebar, halos kasingtaas ng isang "
      + "linya ng teksto. Maganda ang tingin doon ng simpleng marka at ng "
      + "malawak na wordmark. Ang hindi umuubra ay ang "
      + "<strong>detalyadong</strong> larawan — sa sukat na iyon ay lumiliit "
      + "ito hanggang hindi na mabasa, kaya ilagay iyon sa banner sa ibaba. "
      + "Mas maganda kung transparent ang background sa isang light na "
      + "disenyo.",
    logoWorkingMessage: "Pinoproseso…",
    logoUpdatedMessage: "Na-update ang logo.",
    logoRemovedMessage: "Naalis ang logo.",
    logoOversizeMessage: "{{actualKb}} KB ang larawang iyon. Ang limitasyon ay {{limitKb}} KB.",
    removeLogoConfirmTitle: "Alisin ang logo ng bukid?",
    removeLogoConfirmBody:
      "Babalik ang sidebar sa Cluckwork mark. Puwede kang mag-upload ulit "
      + "anumang oras.",
    removeLogoConfirmLabel: "Alisin ang logo",

    // Banner panel (#179)
    bannerSectionHeading: "Banner",
    bannerAlt: "Kasalukuyang banner ng bukid",
    bannerLoadingMessage: "Naglo-load ang banner…",
    bannerLoadFailedMessage: "Hindi na-load ang banner.",
    bannerNoneMessage: "Walang naka-set na banner — nilalaktawan ang splash pagkatapos mag-log in.",
    uploadBannerButton: "Mag-upload ng banner",
    replaceBannerButton: "Palitan ang banner",
    removeBannerButton: "Alisin",
    bannerRulesHint:
      "PNG, JPEG, o WebP, hanggang {{cap}} at 4096 px kada gilid. Ipinapakita "
      + "nang buong-laki sa isang splash pagkatapos mag-log in, isang beses "
      + "kada session — maganda rito ang malawak o detalyadong larawan, "
      + "hindi katulad ng maliit na logo sa sidebar.",
    bannerWorkingMessage: "Pinoproseso…",
    bannerUpdatedMessage: "Na-update ang banner.",
    bannerRemovedMessage: "Naalis ang banner.",
    bannerOversizeMessage: "{{actualKb}} KB ang larawang iyon. Ang limitasyon ay {{limitKb}} KB.",
    removeBannerConfirmTitle: "Alisin ang banner ng bukid?",
    removeBannerConfirmBody:
      "Nilalaktawan ang splash hanggang may bagong banner na ma-upload.",
    removeBannerConfirmLabel: "Alisin ang banner",

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
    defaultStepperUnitLabel: "Yunit ng pagbilang sa Daily Entry",
    defaultStepperUnitHint:
      "Kung magkano ang ibinibilang ng mga +/− na button ng Daily Entry para sa lahat sa "
      + "bukid na ito — halimbawa Tray para magbilang bawat tray (30 itlog) sa halip na "
      + "isa-isang itlog. Maaaring pumili ang bawat tao ng kanilang sarili sa kanilang Account screen.",
    workerSaleAllocationPolicyLabel: "Paglalaan ng benta ng manggagawa",
    workerSaleAllocationPolicyHint:
      "Kinokontrol kung saang mga lote ng itlog maaaring kumuha ng stock ang "
      + "benta ng isang nakatalagang plain Worker. Nakatalagang kawan lang "
      + "ang default; ang mga may-ari at manager ay maaaring pumili ng lahat "
      + "ng kawan sa bukid.",
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
    customFormatOption: "Custom…",
    customDateFormatLabel: "Custom na format ng petsa",
    customTimeFormatLabel: "Custom na format ng oras",
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

    // #308/#360 — muling pagkumpirma ng password (machine-drafted, pending native review)
    stepUpFieldLabel: "Ang kasalukuyan mong password *",
    stepUpCreateHint: "Ang paggawa ng kahit anong user ay nangangailangan muling ilagay ang kasalukuyan mong password.",
    stepUpResetHint: "Ang pag-reset ng password ng kahit sinong user ay nangangailangan muling ilagay ang kasalukuyan mong password.",
    stepUpRoleHint: "Ang pagpapalit ng tungkulin ng kahit sinong user ay nangangailangan muling ilagay ang kasalukuyan mong password.",
    stepUpEmailHint: "Ang pagpapalit ng email sa pag-sign in ay nangangailangan muling ilagay ang kasalukuyan mong password.",
    // #356 — walang kondisyong muling pagkumpirma (machine-drafted, pending native review)
    stepUpDisableHint: "Ang pag-disable ng isang user ay nangangailangan muling ilagay ang kasalukuyan mong password.",
    stepUpEnableHint: "Ang muling pag-enable ng isang user ay nangangailangan muling ilagay ang kasalukuyan mong password.",
    stepUpFlockHint: "Ang pag-assign o pag-alis ng kawan ay nangangailangan muling ilagay ang kasalukuyan mong password.",

    // Users table
    emailColumnHeader: "Email",
    nameColumnHeader: "Pangalan",
    roleColumnHeader: "Tungkulin",
    statusColumnHeader: "Katayuan",
    editButton: "i-edit",
    resetPasswordButton: "password",
    changeRoleButton: "tungkulin",
    changeRoleSubmitButton: "Palitan ang tungkulin",
    changeEmailButton: "palitan ang email",
    changeEmailSubmitButton: "Palitan ang email",
    flocksButton: "mga kawan",
    disabledBadge: "Naka-disable",
    disableButton: "i-disable",
    enableButton: "i-enable",

    // Flock-access dialog
    flockAccessTitle: "Access sa kawan — {{email}}",
    flockAccessHint:
      "Walang assignment = puwedeng magtala ang manggagawa para sa kahit "
      + "anong kawan. Ang unang assignment ang naglilimita sa kanila sa mga "
      + "nakalistang kawan lang.",
    noAssignmentsMessage: "Walang assignment — access sa buong account.",
    removeAssignmentButton: "alisin",
    assignFlockButton: "Mag-assign ng kawan",
    flockLabel: "Kawan",
    selectFlockOption: "Pumili ng kawan",
    doneButton: "Tapos na",
    // #612
    retainedAssignmentsHint:
      "Hindi na plain Worker ang taong ito, kaya walang epekto ang mga "
      + "nakatalagang kawan na ito sa kanyang access. Puwede pa rin itong alisin.",
    inactiveAssignmentLabel: "inactive",
    assignmentsWorkerOnlyHint: "Ang pag-assign ng kawan ay para lang sa plain Worker.",
    farmWideAssignmentLabel: "buong sakahan",
    assignmentFlockUnavailable: "Hindi na available ang kawan na ito.",

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

    // Change-role dialog
    changeRoleTitle: "Palitan ang tungkulin — {{email}}",
    roleDialogHint:
      "Ang pagpapalit ng tungkulin ng isang tao ay nag-sa-sign out sa "
      + "kanila sa lahat ng device. Hindi puwedeng i-demote ang huling "
      + "Owner ng account, at hindi mo mapapalitan ang sarili mong "
      + "tungkulin — humingi ng tulong sa ibang Owner.",

    changeEmailTitle: "Palitan ang email — {{email}}",
    changeEmailHint:
      "Ang address na ito agad ang gagamitin ng user sa susunod na pag-sign in at matatapos ang mga bukas nilang session. "
      + "Walang ipinapadalang confirmation email.",
    loginEmailFieldLabel: "Email sa pag-sign in",
    duplicateEmailMessage: "May user nang gumagamit ng email na ito.",
    lastOwnerEmailMessage: "Magdagdag muna ng pangalawang Owner bago palitan ang sarili mong email sa pag-sign in.",

    // #356 — ang iisang disable/enable dialog (machine-drafted, pending
    // native review): ang dialog mismo ANG kumpirmasyon.
    disableStepUpTitle: "I-disable — {{email}}",
    enableStepUpTitle: "I-enable — {{email}}",
    disableSubmitButton: "I-disable",
    enableSubmitButton: "I-enable",
    disableWarningBody:
      "Ma-si-sign out sila sa lahat ng device kaagad at hindi na "
      + "makakapag-sign in hangga't hindi mo sila na-e-enable ulit.",
    disableReasonFieldLabel: "Dahilan (opsyonal)",

    // Imperative messages
    createSuccessMessage: "Nagawa ang {{role}} account para sa {{email}}.",
    passwordMismatchMessage: "Hindi magkatugma ang mga password.",
    passwordSetMessage: "Naitakda ang password para sa {{email}}. Na-sign out na sila sa lahat ng lugar.",
    updatedMessage: "Na-update ang {{email}}.",
    roleChangedMessage: "Si {{email}} ay {{role}} na ngayon.",
    emailChangedMessage: "Napalitan ang email sa pag-sign in at naging {{email}}.",
    userDisabledMessage: "Na-disable si {{email}}.",
    userEnabledMessage: "Na-enable ulit si {{email}}.",
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
    // #512 T038 — the row-owned flock's exact read failed (machine draft,
    // pending native review).
    flockUnavailable: "Hindi na available ang kawan ng gastos na ito.",
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

    // Edit-customer dialog (#625)
    editButton: "i-edit",
    editCustomerTitle: "I-edit ang {{name}}",

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
    loadMoreButton: "mag-load pa",
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
    conflictRebindFailedMessage:
      "Binago ng ibang tao ang entry na ito at hindi na-load ang "
      + "pinakabagong halaga nito — isara ang form at subukan ulit.",
    gradesMustReconcileMessage:
      "Dapat magkapareho ang mga na-grade na dami sa kabuuang itlog na bawas ang basag, marumi, at tinapon.",
    entryAdjustedMessage: "Na-adjust ang entry — na-update ang stock at ang rekord ng manok para tumugma.",
    voidConfirmTitle: "I-void ang entry ng {{date}} para sa {{flock}}?",
    voidConfirmBody:
      "Mawawalan ng laman ang mga lote ng itlog nito at mababaliktad ang "
      + "mga namatay dito. Mananatiling Na-void ang entry. Tatanggihan "
      + "kung nabenta na ang alinman sa mga itlog nito.",
    voidConfirmLabel: "I-void ang entry",
    entryVoidedMessage: "Na-void ang entry — nawalan ng laman ang mga lote ng itlog nito at nabaliktad ang mga namatay dito.",
    voidConflictMessage: "Binago ng ibang tao ang entry na ito — tingnan ang listahan at subukan ulit.",

    // Filters
    flockLabel: "Kawan",
    allFlocksOption: "Lahat ng kawan",
    fromLabel: "Mula",
    toLabel: "Hanggang",
    rowFlockUnavailable: "Hindi na available ang kawan na ito.",

    // Adjust dialog
    adjustDialogTitle: "I-adjust ang entry",
    adjustDialogTitleWithEntry: "I-adjust — {{date}}, {{flock}}",
    previouslyAdjusted:
      "Na-adjust na dati (kabuuan {{total}}, mortalidad {{mortality}} — \"{{reason}}\").",
    // Ang dalawang hakbang, mga label ng bilang at ang reconciliation chip ay
    // galing sa `dailyEntry` namespace (iisang form).
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
    // #396 — machine-drafted, pending native review (#182).
    conditionHeader: "May depekto",
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
    // #396 — basag/marumi na itlog na naging stock sa halip na malugi.
    // Machine-drafted, pending native review (#182).
    conditionHeader: "May depekto",
    deathsHeader: "Namatay",
    henDaysHeader: "Hen-days",
    henDayPctHeader: "Hen-day %",
    periodRowLabel: "Panahon",
    gradeTotalsLabel: "Ayon sa grado:",

    moneyHeading: "Pera",
    salesRowLabel: "Benta",
    salesSummary:
      "{{confirmed}} kumpirmadong order — kita {{revenue}}, nabayaran {{paid}}, nakabinbin {{outstanding}}",
    salesVoidedSuffix: " ({{voided}} na-void)",
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
    scopedHeading: "Kasaysayan ng {{entityType}}",
    scopedHeadingFallback: "Kasaysayan ng record",
    entityTypeFilterLabel: "Uri ng record",
    allEntityTypesOption: "Lahat ng uri",
    actionFilterLabel: "Aksyon",
    allActionsOption: "Lahat ng aksyon",
    whenHeader: "Kailan (UTC)",
    whoHeader: "Sino",
    actionHeader: "Aksyon",
    entityHeader: "Entidad",
    reasonHeader: "Dahilan",
    emptyMessage: "Wala pang audit event.",
    scopedEmptyMessage: "Wala pang audit event para sa record na ito.",
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

    // worker sale-allocation policy — #612
    "workerSaleAllocationPolicy.AssignedFlocksOnly": "Nakatalagang kawan lang",
    "workerSaleAllocationPolicy.AllFarmFlocks": "Lahat ng kawan sa bukid",

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
    "auditAction.User.BreakGlassReset": "Emergency na pag-reset ng password",
    "auditAction.User.RoleChanged": "Binago ang tungkulin",
    "auditAction.User.EmailChanged": "Binago ang email sa pag-sign in",
    "auditAction.User.Disabled": "Na-disable ang user",
    "auditAction.User.Enabled": "Na-enable ang user",
    "auditAction.User.FlockAssign": "Na-assign ang kawan sa user",
    "auditAction.User.FlockUnassign": "Na-unassign ang kawan mula sa user",
    "auditAction.Account.Export": "Na-export ang datos",
    "auditAction.Account.SetLogo": "Na-set ang logo ng bukid",
    "auditAction.Account.RemoveLogo": "Naalis ang logo ng bukid",
    "auditAction.Account.SetBanner": "Na-set ang banner ng bukid",
    "auditAction.Account.RemoveBanner": "Naalis ang banner ng bukid",
    "auditAction.Account.UpdateSettings": "Na-update ang mga setting ng bukid",
    "auditAction.Account.Suspend": "Sinuspinde ang bukid",
    "auditAction.Account.Reactivate": "Muling binuksan ang bukid",
    "auditAction.Account.Provisioned": "Ginawa ang bukid",
    "auditAction.Product.Create": "Nagawa ang produkto",
    "auditAction.Product.Update": "Na-update ang produkto",
    "auditAction.Product.Activate": "Na-activate ang produkto",
    "auditAction.Product.Deactivate": "Na-deactivate ang produkto",
    "auditAction.EggUnitConversion.Update": "Na-update ang conversion ng yunit ng itlog",
    "auditAction.EggLot.Movement": "Na-write off / na-recount ang stock",
    "auditAction.Flock.Create": "Nagawa ang kawan",
    "auditAction.DailyEntry.Create": "Nagawa ang pang-araw-araw na entry",
    "auditAction.DailyEntry.Update": "Na-edit ang draft ng pang-araw-araw na entry",
    "auditAction.DailyEntry.Submit": "Naisumite ang pang-araw-araw na entry",
    "auditAction.SalesOrder.AddItem": "Nagdagdag ng linya sa order",
    "auditAction.SalesOrder.UpdateItem": "Binago ang linya ng order",
    "auditAction.SalesOrder.RemoveItem": "Inalis ang linya ng order",
    "auditAction.SalesOrder.Create": "Nagawa ang order ng benta",
    "auditAction.SalesOrder.Confirm": "Nakumpirma ang order ng benta",
    "auditAction.SalesOrder.Cancel": "Kinansela ang order ng benta",
    "auditAction.Expense.Create": "Nagawa ang gastos",
    "auditAction.EggGrade.Create": "Nagawa ang grado ng itlog",
    "auditAction.Customer.Create": "Nagawa ang customer",
    "auditAction.Customer.Update": "Na-update ang customer",

    "entityType.Account": "Account",
    "entityType.Customer": "Customer",
    "entityType.DailyEntry": "Araw-araw na Entry",
    "entityType.EggGrade": "Grado ng Itlog",
    "entityType.EggLot": "Lote ng Itlog",
    "entityType.EggUnitConversion": "Conversion ng Yunit ng Itlog",
    "entityType.Expense": "Gastos",
    "entityType.ExpenseCategory": "Kategorya ng Gastos",
    "entityType.FarmLogo": "Logo ng bukid",
    "entityType.Flock": "Kawan",
    "entityType.InventoryItem": "Item sa Imbentaryo",
    "entityType.Payment": "Bayad",
    "entityType.Product": "Produkto",
    "entityType.SalesOrder": "Order ng Benta",
    "entityType.User": "User",
    "entityType.WaterUsage": "Paggamit ng Tubig",
  },

  // machine-drafted (#182) — pending native review. Task 32 (B6a): HelpPage
  // prose, getting-around through mistakes (INCLUDING the "Fixing mistakes"
  // table). Keys mirror en.ts help exactly, including every <strong>/<em>
  // tag — catalogParity enforces tag parity per key. Screen names in prose
  // reuse this pack's existing nav labels where one exists (e.g. "History",
  // "Stock", "Audit", "Export" kept as English loanwords, "Araw-araw na
  // Tala" for Daily entry, "Mga Kawan" for Flocks) — see nav above.
  // Domain-verb loanwords ("i-void", "i-adjust", "isumite", "i-archive",
  // "ubusin"/"naubos", "kumpirmahin") follow the precedent set in
  // sales/history/flocks above. "Currency"/"Time zone"/"Locale"/"Logo"/
  // "Account"/"Stock"/"History"/"Audit"/"Export"/"Glossary" kept as English
  // loanwords, same treatment as settings/account above (flag for native
  // review). The glossary table (h3 id="glossary") is translated separately,
  // near the end of this block (Task 33, B6b) — tocGlossary above is just
  // the rail's link text.
  help: {
    eyebrow: "Gabay ng user",
    heading: "Tulong",
    lead: "Kung paano gumagana ang Cluckwork, screen by screen — at kung paano mo aayusin ang mga pagkakamali.",
    contentsAriaLabel: "Nilalaman ng tulong",
    contentsEyebrow: "Nilalaman",
    // #657 — paghahanap, nakagrupong index at nakagrupong glossary.
    searchLabel: "Maghanap sa gabay",
    searchPlaceholder: "Mag-type ng termino o paksa",
    searchClear: "I-clear ang paghahanap",
    searchMatches: "Mga tugma para sa “{{query}}”: {{sections}} sa gabay, {{terms}} sa glossary.",
    searchNoMatches: "Walang tumutugma sa “{{query}}”.",
    searchShortcutHint: "Pindutin ang / para maghanap",
    glossaryJumpAriaLabel: "Mga grupo ng glossary",
    openScreen: "Buksan ang {{screen}}",
    railGroupStartHere: "Magsimula rito",
    railGroupEveryDay: "Araw-araw",
    railGroupSelling: "Pagbebenta",
    railGroupSupplies: "Mga suplay",
    railGroupFarm: "Farm at mga tao",
    railGroupApp: "Ang app",
    glossaryGroupGettingAround: "Paglilibot sa app",
    glossaryGroupSigningIn: "Pag-sign in at kung sino ang may pahintulot",
    glossaryGroupFlocksEntry: "Mga flock at pang-araw-araw na entry",
    glossaryGroupEggsStock: "Mga itlog, grade at stock",
    glossaryGroupSalesMoney: "Benta at pera",
    glossaryGroupSupplies: "Feed, tubig at mga suplay",
    glossaryGroupFarm: "Mga setting at branding ng farm",

    tocGettingAround: "Pag-navigate",
    tocSigningIn: "Pag-sign in",
    tocDailyLoop: "Ang pang-araw-araw na siklo",
    tocRoles: "Sino ang puwedeng gumawa ng ano",
    tocDialogs: "Pagdagdag at pagtatama",
    tocDailyEntry: "Araw-araw na Tala",
    tocFlocks: "Mga Kawan at Ibon",
    tocGrades: "Mga Grado ng Itlog",
    tocProducts: "Mga Produkto",
    tocStock: "Stock",
    tocInventory: "Mga suplay at imbentaryo",
    tocFeed: "Pakain",
    tocWater: "Tubig",
    tocSales: "Mga Customer at Benta",
    tocReports: "Mga Report",
    tocExpenses: "Mga Gastos",
    tocHistory: "History",
    tocAudit: "Audit Log",
    tocExport: "Export at Backup",
    tocFarmSettings: "Mga Setting ng Bukid",
    tocFarmPalette: "Paleta ng Bukid",
    tocAccount: "Ang Iyong Account",
    tocInstall: "Pag-install sa Telepono",
    tocMistakes: "Pagtatama ng mga Pagkakamali",
    tocGlossary: "Glossary",

    gettingAroundHeading: "Pag-navigate",
    gettingAroundSidebar:
      "Sa computer, nasa <strong>sidebar</strong> sa kaliwa ang bawat screen, nakagrupo ayon sa trabaho.",
    gettingAroundTabs:
      "Sa telepono, ang mga screen na pinakamadalas mong gamitin ay lumalabas bilang <strong>mga tab sa "
      + "ibaba</strong>, malapit sa hinlalaki. Depende sa tungkulin mo kung alin sa apat ang makukuha mo — "
      + "makukuha ng manggagawa ang Araw-araw na Tala, makukuha ng Benta ang Benta. Isang tap na lang ang "
      + "lahat ng iba pa, sa ilalim ng <strong>Higit Pa</strong>.",
    gettingAroundPageLoading:
      "Sa unang pagkakataong magbukas ka ng screen pagkatapos simulan o i-update ang Cluckwork, maaaring sandaling lumabas ang mensahe ng pag-load habang binubuksan ang screen. Magagamit pa rin ang navigation; hintaying lumabas ang screen.",
    gettingAroundErrorScreen:
      "Kapag nagpakita ang isang screen ng <strong>\"May Nangyaring Mali\"</strong>, ibig sabihin ay "
      + "na-catch ng app ang isang error sa halip na mag-iwan sa iyo ng blangkong page. Ligtas ang anumang "
      + "na-save mo na (maaaring kailanganin mong i-type ulit ang kahit anong tina-type mo pa) — i-tap ang "
      + "<strong>I-reload</strong>, o <strong>Bumalik sa dashboard</strong> at subukan ulit. Kung "
      + "paulit-ulit itong nangyayari, buksan ang \"Mga detalye ng error\" at magpadala ng screenshot.",
    gettingAroundWhereMessagesAppear:
      "Ang mensahe tungkol sa isang bagay na nabigo ay lumalabas <strong>kung nasaan ang trabaho</strong>: "
      + "kung pumupuno ka ng pop-up na form, lalabas ito sa loob ng form na iyon, katabi ng mga field na "
      + "tinutukoy nito; kung ang screen mismo ang nabigo — halimbawa, isang listahang hindi ma-load — lalabas "
      + "ito sa screen sa likod. Kaya't ang isang form na ayaw mag-save ay laging nagsasabi kung bakit nang "
      + "hindi nagsasara. Kapag isinara mo ang form, nawawala ang mensahe nito: tapos na ang pagsubok na iyon, "
      + "at nananatili ang anumang iniulat ng screen mismo.",
    gettingAroundSearchablePicker:
      "<strong>Picker na may search</strong> ang mga field ng pangalan ng kawan at customer: mag-type para mag-search, gamitin "
      + "ang arrow keys o pointer para tingnan ang mga resulta, at pindutin ang <strong>Enter</strong> o "
      + "i-click ang isang resulta para piliin ito. Ang pag-type ay nag-e-explore lamang — nananatili ang "
      + "dati mong napili hangga't wala kang bagong pinili o pinindot mo ang <strong>Escape</strong> para "
      + "kanselahin. Ipinapakita ng mahabang listahan ang <strong>Mag-load ng higit pa</strong>; ipinapakita "
      + "ng <strong>Subukan ulit</strong> ang isang search o load-more na nabigo. Kapag hindi na mahanap ang "
      + "naalala o naka-link na pangalan, ipinapakita ng field ang <strong>Hindi available</strong> kasama "
      + "ang Subukan ulit.",

    signingInHeading: "Pag-sign in",
    signingInBasic:
      "Mag-sign in gamit ang <strong>code ng bukid</strong> mo, at pagkatapos ang email at password na "
      + "ni-set up ng iyong administrator. Ang maling password ay nagsasabi lang na "
      + "<strong>Mali ang email o password</strong> — subukan ulit.",
    signingInRateLimit:
      "Para pabagalin ang sinumang nag-guess ng password, <strong>limitado</strong> ang mga pagsubok sa "
      + "pag-sign in mula sa parehong lugar. Pagkatapos ng sobrang dami ng subok sa loob ng ilang minuto, "
      + "makikita mo ang <strong>\"Sobra na ang subok sa pag-sign in\"</strong> — hindi iyon isang error, "
      + "maghintay lang ng ilang minuto at subukan ulit. Hindi kailanman naaapektuhan ang pagiging <em>naka-"
      + "sign in</em> na; normal na tumutuloy ang trabaho mo.",
    signingInAccountLock:
      "Bukod dito, ang sobrang dami ng maling password para sa <em>isang account</em> ay panandaliang "
      + "nagba-block sa <em>account</em> na iyon. Habang naka-block, kahit ang tamang password ay nagsasabi "
      + "pa rin ng <strong>Mali ang email o password</strong>. Pansamantala lang ang block — maghintay ng "
      + "hanggang 15 minuto at subukan ulit.",
    signingInPersistence:
      "Ligtas na naka-save sa browser mo ang sign in mo at nananatiling aktibo habang nagtatrabaho ka, "
      + "kahit mag-reload at kahit bukas ang app sa <strong>ilang tab</strong> nang sabay-sabay. "
      + "Pagkatapos ma-<strong>update</strong> ang app, maaaring hilingin sa iyong mag-sign in muli — "
      + "normal lang iyon.",
    // machine-drafted (#532) — pending native review.
    signingInMultiTabResync:
      "May sariling ligtas na session ang bawat bukid sa browser na ito, kaya hindi na pinapalitan ng mga "
      + "bukid na bukas sa magkakaibang <strong>tab</strong> ang session ng isa't isa. Naaalala ng tab ang bukid "
      + "nito kapag nag-reload. Ang tab na walang natatandaang bukid at makakakita ng ilang session ay babalik pa rin "
      + "sa pag-sign in sa halip na manghula — piliin ang code ng bukid at mag-sign in. Naaalala rin ng page ng "
      + "pag-sign in ang mga code ng bukid na ginamit mo sa device na ito at inaalok ang mga ito bilang isang "
      + "pumipili, at ang link na ?farm= ay nag-pre-fill sa field. Ang bawat natatandaang bukid ay maaaring "
      + "alisin sa listahan gamit ang kanyang kontrol na Kalimutan; sa paggawa nitong hindi binubura ang session ng "
      + "ibang bukid o iba pang setting ng device. Walang session ng ibang bukid na binubura.",
    // machine-drafted (#283) — pending native review.
    signingInFirstRun:
      "<strong>Unang pag-sign in sa isang bagong-bagong farm.</strong> Walang default na password — "
      + "nagpapatakbo ang isang operator ng isang beses na setup command na naglilimbag ng pansamantalang "
      + "password at ang code ng bukid. Mag-sign in gamit ang dalawa at diretso kang mapupunta sa isang screen ng "
      + "<strong>Itakda ang iyong "
      + "password</strong> sa halip na ang normal na app; wala pang gumagana hangga't hindi ka pumipili ng "
      + "sarili mong password doon. Iba ito sa karaniwang <em>Pagbabago ng password</em>. Hangga't hindi pa "
      + "naisasagawa ang hakbang na iyon sa setup, sinasabi ito sa iyo kapag sinubukan mong mag-sign in at "
      + "itinuturo ka sa kung sino ang nangangasiwa ng server, sa halip na sabihing mali ang iyong mga detalye.",
    // #308/#356/#360 (machine-drafted, pending native review)
    signingInStepUp:
      "May walong aksyon sa <strong>Users</strong> screen na humihiling sa iyong <strong>muling ilagay ang "
      + "kasalukuyan mong password</strong> mismo sa dialog: ang paggawa ng kahit anong user, ang pag-reset ng "
      + "password ng kahit sinong user, ang pagpapalit ng tungkulin ng kahit sinong user, ang pagpapalit ng email "
      + "sa pag-sign in, ang pag-disable ng user, ang muling pag-enable ng user, ang pag-assign ng manggagawa sa "
      + "isang kawan, at ang pag-alis ng assignment ng manggagawa sa isang kawan. Kinukumpirma nito na ikaw "
      + "talaga bago magbigay o mag-alis ng access. Hindi muling hinihingi ito sa mga pagbabago ng display name.",
    signingInCredentialEpoch:
      "Kapag ni-reset ng administrator ang password, maaaring agad ma-invalid ang kasalukuyan mong sign-in. Kung "
      + "makakita ka ng mensaheng nagbago ang iyong credentials, mag-sign in muli gamit ang kasalukuyan mong password.",
    interfaceLanguage:
      "<strong>Wika ng interface.</strong> Kahit sino ay puwedeng pumili ng wikang gagamitin sa interface "
      + "mula sa <strong>Account → Mga Kagustuhan</strong> — English, Español, o Tagalog. Isang patuloy na "
      + "trabaho ang pagsasalin: ang mga screen ng sign-in at benta, ang mga error message, at ang "
      + "<strong>Account → Mga Kagustuhan</strong> mismo ay isinalin na ngayon; ang natitirang bahagi ng "
      + "Account screen (kasama ang password section) at ang natitirang app ay isinasalin pa nang screen by "
      + "screen. Hangga't hindi pa naisasalin ang isang screen, ipinapakita lang ito sa English, anuman ang "
      + "wikang pinili mo.",

    dailyLoopHeading: "Ang pang-araw-araw na siklo",
    dailyLoopChain:
      "Ang lahat sa Cluckwork ay nakasalalay sa iisang chain: nagtatala ka ng <strong>araw-araw na "
      + "entry</strong> para sa bawat kawan (itlog ayon sa grado, nawala, namatay), <strong>isinusumite</strong> "
      + "mo ito, at ang pagsumite ay lumilikha ng may petsang <strong>lote ng itlog</strong> — iyon ang iyong "
      + "nabibiling <strong>stock</strong>. Ang isang <strong>order ng benta</strong> ay kumukuha mula sa "
      + "stock kapag kinumpirma mo ito, palaging ang pinakalumang itlog muna. Gayon din ang daloy ng feed sa "
      + "panig ng input: nagdadagdag ng feed sa stock ang mga pagbili, binabawasan ito ng araw-araw na "
      + "paggamit kada kawan.",
    dailyLoopSummary: "Magtala ng entry → isumite → lote ng itlog → stock → order → kumpirmahin.",

    rolesHeading: "Sino ang puwedeng gumawa ng ano",
    rolesWorkers:
      "Limang uri ng sign-in. Isinasagawa ng mga <strong>Manggagawa</strong> ang pang-araw-araw na siklo — "
      + "nagtatala at nagsusumite ng entry, tumatanggap ng feed, nagtatala ng paggamit ng feed at tubig, "
      + "gumagawa ng customer, humahawak ng order mula draft hanggang kumpirmasyon. Puwedeng "
      + "limitahan ang isang manggagawa sa <strong>mga naka-assign na kawan</strong>: kung walang "
      + "assignment, puwede siyang magtala para sa kahit anong kawan; ang unang assignment ang naglilimita "
      + "sa kanya sa mga nakalista lang. Hindi puwedeng gumawa ng kawan ang mga manggagawa — "
      + "administrasyon iyon ng May-ari/Manager.",
    rolesManagers:
      "Ginagawa ng mga <strong>Manager</strong> ang lahat ng ginagawa ng manggagawa, dagdag pa ang lahat "
      + "ng <strong>nag-a-undo, nagtatama, o nag-co-configure</strong>: void, pagtatama ng stock at tubig, "
      + "paggawa ng kawan at mga pagbabago sa lifecycle ng kawan, pag-cull, ang mga katalogo ng "
      + "grado/produkto/item, gastos, report ng pera, audit log, at export.",
    rolesSalesReadOnly:
      "Hinahawakan ng mga sign-in na <strong>Benta</strong> ang customer, order, at <strong>bayad</strong> "
      + "— pero walang production capture at walang gastos. Nakikita ng mga sign-in na <strong>Read-"
      + "only</strong> ang stock, history, at report, at hindi puwedeng magbago ng anuman.",
    rolesAdmin:
      "Ginagawa ng <strong>Admin (may-ari)</strong> ang lahat ng ginagawa ng manager at siya lang ang "
      + "tungkuling namamahala ng mga user: gumagawa ng sign-in sa screen na <strong>Mga User</strong> "
      + "(email, password, opsyonal na pangalan, at tungkulin) at nag-a-assign ng manggagawa sa mga kawan. "
      + "Puwedeng baguhin sa ibang pagkakataon ang pangalan ng isang user mula sa aksyong "
      + "<strong>i-edit</strong> ng row, at nagtatakda ang aksyong <strong>password</strong> ng nakalimutang "
      + "password nang hindi kailangan ang luma. Ang aksyong <strong>tungkulin</strong> ay nag-a-promote o "
      + "nag-de-demote ng isang existing na user sa isa sa limang tungkulin — tinatanggihan ang pagtarget "
      + "sa sarili mong sign-in, at tinatanggihan ang pag-demote sa huling Admin (may-ari) ng account, para "
      + "hindi kailanman maiwan ang farm na walang pamamahala ng user. Ang paggawa ng kahit anong sign-in, "
      + "pag-reset ng password ng kahit sinong user, at pagpapalit ng tungkulin ng kahit sinong user ay "
      + "humihiling sa naka-sign-in na Admin (may-ari) na muling ilagay ang kasalukuyan niyang password. Ang "
      + "pagbabago ng tungkulin ay nag-si-sign out sa apektadong sign-in kahit saan sa susunod nitong "
      + "request, gaya ng pag-reset ng password. Nakatago ang mga control na hindi mo puwedeng gamitin, at "
      + "tinatanggihan pa rin ito ng server.",
    // #356 (machine-drafted, pending native review)
    rolesDisableUser:
      "Ang <strong>pag-disable ng sign-in</strong> (Users screen, Admin/owner lang) ay agad na pumuputol ng "
      + "access — bawat bukas na session ng taong iyon ay natatapos sa susunod nitong request, gaya rin ng "
      + "pagbabago ng tungkulin o pag-reset ng password. Opsyonal ang dahilan, at kahit alin pa, napupunta "
      + "ito sa audit log. Ibinabalik ng <strong>Muling i-enable</strong> ang kanyang kakayahang mag-sign "
      + "in, pero hindi na buhayin pa ang mga session na natapos ng pag-disable — mag-si-sign in sila ulit "
      + "gamit ang kasalukuyan nilang password. Hindi mo puwedeng i-disable ang sarili mong sign-in, at "
      + "hindi mo puwedeng i-disable ang huling Admin (may-ari) ng account.",
    rolesChangeEmail:
      "Maaaring agad palitan ng mga Owner ang <strong>email sa pag-sign in</strong> ng isang user sa Users "
      + "screen. Ang bagong address ang gagamitin sa susunod na pag-sign in, matatapos ang lahat ng bukas na "
      + "session, at walang ipinapadalang confirmation email. Kailangan ng isa pang aktibong Owner para palitan "
      + "ang sarili mong address.",
    ownPassword:
      "<strong>Ang sarili mong password.</strong> Kahit sino, sa kahit anong tungkulin, ay puwedeng "
      + "magpalit ng sariling password sa screen na <strong>Account</strong> sa pamamagitan ng pag-enter ng "
      + "kasalukuyan at bagong password (hindi bababa sa 12 karakter). Ang pagpapalit ng sarili mong password "
      + "ay nagpapanatiling naka-sign in sa device na ito gamit ang bagong credentials at nagpapatigil sa bawat "
      + "<em>ibang</em> bukas na session sa susunod nitong request. Kung admin ang magse-set ng password mo, "
      + "titigil ang lahat ng bukas mong session sa susunod nitong request.",

    dialogsHeading: "Pagdagdag at pagtatama",
    dialogsPopup:
      "Nangyayari ang pagdadagdag at pagtatama sa isang popup. Hanapin ang button na <strong>Bago …</strong> "
      + "sa tabi ng titulo ng screen — bagong grado, produkto, customer, kawan, item, user, order. Ang link "
      + "na <strong>i-edit</strong> o <strong>tama</strong> ng bawat row ay nagbubukas ng parehong popup na "
      + "may punan na ang mga value ng row na iyon.",
    dialogsDrillDowns:
      "Gayon din ang mga drill-down. Buksan ang talaan ng <strong>ibon</strong> ng isang kawan para "
      + "magtala ng cull, ang isang item sa imbentaryo para magtala ng pagbili, paggamit ng feed, o "
      + "pagtatama ng stock, ang isang order para <strong>magtala ng bayad</strong>, o ang <strong>mga "
      + "kawan</strong> ng isang manggagawa para pamahalaan ang access niya — nananatili sa lugar nito ang "
      + "talaan at ang form na ang lalapit sa iyo.",
    dialogsCancel:
      "Ang <strong>Kanselahin</strong>, Escape, o pag-click sa labas ay nagsasara ng popup, walang "
      + "itinatala, at binubura ang na-type mo — kapag binuksan mong muli, blangko na ang form. Kung "
      + "nabigo ang pag-save, mananatiling bukas ang popup na kasama ang mga value at ang dahilan mo, "
      + "para maayos mo ito at subukan ulit — ligtas ang muling pagsubok, hindi ito kailanman nagtatala "
      + "ng parehong bagay nang dalawang beses.",
    dialogsModal:
      "Habang bukas ang popup, <strong>naghihintay</strong> ang page sa likod nito: ang mga click, ang Tab "
      + "key at ang screen reader ay nananatili sa loob ng form, kaya walang nababago sa likod nang hindi "
      + "sinasadya, at isinasara ng Escape ang popup na pinagtatrabahuhan mo.",
    dialogsInlineForms:
      "Ang mga screen na ang buong trabaho ay pagtatala ay pinapanatili ang form sa page mismo: "
      + "<strong>Araw-araw na Tala</strong>, <strong>Tubig</strong>, pagtatala ng gastos, at pagdagdag ng "
      + "linya sa isang draft na order. Ang mga ito ang ginagamit mo araw-araw — walang dagdag na click.",
    dialogsSteppers:
      "Ang mga bilang na buong numero — bilang ng itlog, bilang ng ibon, dami ng benta, itlog bawat unit — "
      + "ay may <strong>−</strong> at <strong>+</strong> na button na kasya sa hinlalaki: i-tap para sa isa, "
      + "<strong>pindutin nang matagal</strong> para bumilis. Ang dami ng isang linya ng benta ay hindi "
      + "bababa sa 1 at palaging <strong>buong numero</strong>, gamit man ang button o tina-type. Ang mga "
      + "desimal ay para sa presyo, na tina-type.",
    dialogsConfirm:
      "<strong>Nagtatanong muna ang mga aksyong hindi na maaaring i-undo.</strong> Ang pagsumite ng isang "
      + "araw, pagkumpirma o pagkansela ng order, pag-ubos o pag-archive ng kawan — sinasabi ng bawat isa "
      + "kung ano ang malapit nang mangyari at naghihintay. Nagsisimula ang keyboard sa "
      + "<strong>Kanselahin</strong>, para hindi tuluyang tumuloy ang pagpindot ng Enter dahil sa ugali. "
      + "Ang <strong>pulang</strong> button ay nangangahulugang ini-undo o inaalis ng aksyon ang isang "
      + "bagay: pag-void, pagkansela ng draft, pag-ubos, pag-archive. Ang pagsumite ng isang araw at "
      + "pagkumpirma ng order ay hindi rin puwedeng i-undo, pero ito ang normal na paraan sa buong linggo.",
    dialogsVoidReason:
      "<strong>Kailangan ng dahilan ang mga void.</strong> Ang pag-void ng isang araw-araw na entry, isang "
      + "bayad, o isang nakumpirmang order ay nagtatanong sa parehong paraan pero humihiling muna ng "
      + "nakasulat na dahilan — naitatala ito kasama ang void at ipinapakita saanman lumabas ang rekord na "
      + "iyon pagkatapos, kaya isulat kung ano talaga ang nangyari. Iwanan itong blangko at sasabihin agad "
      + "ito ng popup, pinapanatili ang anumang na-type mo na.",

    dailyEntryHeading: "Araw-araw na Tala",
    dailyEntryPanes:
      "Piliin ang kawan at petsa sa itaas, pagkatapos ay magtrabaho sa dalawang panel nang magkatabi: "
      + "<strong>1 Bilang ng itlog</strong> (kabuuan, basag, marumi, tinapon, namatay) at <strong>2 "
      + "Pag-grade</strong>. Ang mga bilang ay lumilikha ng isang <strong>naibibentang</strong> figure, at "
      + "iyon ang numerong dapat abutin ng mga grado. Puwedeng iwan itong bahagya o hindi man simulan para "
      + "sa isang draft — para sa Isumite, kailangan itong eksakto.",
    dailyEntryGradingDown:
      "<strong>Bumababa</strong> ang bilang sa pag-grade. Sa tabi ng mga grado ay makikita kung ilang "
      + "naibibentang itlog pa ang kailangan mong ilagay; nagiging berde ito sa sandaling tumugma ang araw "
      + "at pula kung lumagpas ka — ang paglagpas ay humaharang din sa pag-save ng draft, hindi lang sa "
      + "Isumite. Hindi ka puwedeng magsumite hangga't hindi umaabot sa eksaktong zero — okay lang na "
      + "bahagya o hindi man i-grade ang araw para sa draft, pero hindi para sa Isumite.",
    dailyEntryButtons:
      "May mga button na <strong>−</strong> at <strong>+</strong> ang bawat bilang. I-tap para sa isa, o "
      + "<strong>i-hold</strong> — bumibilis ito habang tumatagal, kaya ilang daang itlog ay tumatagal lang "
      + "ng isang segundo. Mas madali kaysa sa keypad kapag naka-guwantes. Hindi na huminto ang "
      + "<strong>+</strong> ng isang grado sa kasalukuyang kabuuan ng araw — bilangin muna ang mga grado at "
      + "aabot ang kabuuan para tumugma. Tumataas lang ito, hindi kailanman bumababa, kaya ang pagbawas sa "
      + "kabuuan sa hakbang 1 ay hindi kailanman itutulak pababa ang isang grado. Ang mga bukid na "
      + "nagbibilang bawat tray ay maaaring gawing isang buong pack unit ang bilang ng bawat tap sa halip "
      + "na isang itlog — nasa <strong>Settings</strong> ang default ng bukid, at maaaring pumili ang bawat "
      + "tao ng kanilang sarili sa kanilang <strong>Account</strong> screen. Kapag may pack unit na "
      + "ginagamit, sinasabi mismo ng mga button (<strong>−30 / +30</strong>) at may tala sa itaas ng mga "
      + "pane na nagsasabi ng yunit; ang pag-type ay naglalagay pa rin ng eksaktong numero.",
    dailyEntryPutAllIn:
      "Karamihan sa mga araw ay natatapos sa parehong paraan — isang grado na lang ang kumukuha ng "
      + "natitira. Ginagawa ito sa isang galaw ng <strong>Ilagay lahat sa…</strong> sa tabi ng natitirang "
      + "bilang: i-drag ito papunta sa isang grado, o i-tap ito at pumili ng isa.",
    dailyEntrySaveBar:
      "Nananatili ang dalawang save button sa isang bar sa ibaba ng screen habang nag-scroll ka. Sa "
      + "telepono, ipinapakita rin ng bar na iyon ang naibibentang bilang at kung ilan pa ang natitira, "
      + "para hindi ka kailanman mawalan ng tingin kung tumutugma ang araw.",
    dailyEntrySaveSubmit:
      "Pinapanatiling editable ang araw ng <strong>I-save ang draft</strong>. Ginagawa itong opisyal ng "
      + "<strong>Isumite</strong>: nililikha nito ang mga lote ng itlog ng araw at itinatala ang namatay sa "
      + "talaan ng ibon ng kawan. Hindi na ito puwedeng i-edit ng manggagawa — puwede itong i-adjust o "
      + "i-void ng admin (tingnan ang \"Pagtatama ng mga Pagkakamali\").",
    dailyEntryLocking:
      "<strong>Awtomatikong naka-lock ang mga naisumiteng entry pagkalipas ng 7 araw</strong>. Ang pagiging "
      + "naka-lock ay nangangahulugan lang na lumipas na ang correction window para sa mga karaniwang ayos "
      + "— gumagana pa rin ang pag-adjust/void ng admin sa mga naka-lock na entry.",
    dailyEntryToday:
      "Ang ibig sabihin ng \"Ngayon\" ay <strong>ang ngayon ng bukid mo</strong>, hindi ang oras sa ibang "
      + "bahagi ng mundo. Puwede kang magtala ng kahit anong araw hanggang at kasama ito; tatanggihan ang "
      + "isang araw na hindi pa nangyayari sa bukid — dito at sa lahat ng lugar na naglalagay ka ng petsa: "
      + "paggamit ng feed at tubig, pagbili ng feed at pagtatama ng stock, gastos, bayad, at petsa ng "
      + "paglagay ng kawan. Ang parehong petsa ang nagdedesisyon kung kailan lumalabas ang itlog sa isang "
      + "withdrawal period, kung anong itlog ang puwedeng kunin ng isang benta, ang araw ng pag-ubos o "
      + "pag-archive ng isang kawan, at ang range na binubuksan ng mga report — para walang hindi "
      + "magkatugmang araw.",
    dailyEntryOnePerDay:
      "Isang entry kada kawan kada araw. Ang muling pagbukas ng isang araw na may draft ay nagloload nito "
      + "para sa pag-edit at nagpapakita ng badge na <strong>Ina-edit ang draft</strong> sa tabi ng titulo, "
      + "para hindi kailanman kamukha ng pagsisimula mula sa wala ang pagbabalik sa na-save na trabaho. "
      + "Kung nabigo ang prefill, naka-block ang pag-save hanggang magtagumpay ito, para hindi kailanman "
      + "ma-overwrite nang tahimik ang isang existing na draft.",
    dailyEntryDepletedBackfill:
      "Tumatanggap ang mga naubos na kawan ng backfilled na entry hanggang sa petsa ng pag-ubos nila; "
      + "walang tinatanggap ang mga naka-archive na kawan.",

    flocksHeading: "Mga Kawan at Ibon",
    flocksCurrentBirds:
      "Ang <strong>kasalukuyang ibon</strong> ng isang kawan = ang paunang bilang nito bawas ang lahat sa "
      + "<strong>talaan ng ibon</strong> nito: namatay (awtomatikong idinaragdag kapag naisumite ang mga "
      + "entry), <strong>cull</strong> (sadyang inalis na ibon — naibenta, pinatay, ibinigay), at manual na "
      + "<strong>adjustment</strong> (pagtatama ng bilang, sa kahit anong direksyon).",
    flocksLifecycle:
      "Lifecycle: <strong>Aktibo</strong> (normal) → <strong>Naubos</strong> (wala nang ibon; nananatili "
      + "ang history, pinapayagan ang backfill) → <strong>Naka-archive</strong> (nakatago mula sa "
      + "pang-araw-araw na trabaho). Humihingi ng kumpirmasyon ang pag-ubos at pag-archive; parehong "
      + "puwedeng i-reverse gamit ang <strong>Reactivate</strong>.",
    flocksPermissions:
      "Gumagawa ng kawan ang May-ari at Manager. Kahit sino ay puwedeng tumingin sa mga kawan na "
      + "pinapayagan niyang makita at sa kanilang talaan ng ibon. Ang pag-edit ng kawan, mga "
      + "pagbabago sa lifecycle, at pagtatala ng cull/adjustment ay para sa admin lang.",

    gradesHeading: "Mga Grado ng Itlog",
    gradesBuckets:
      "Ang mga grado ang mga grading bucket ng bukid mo — sukat (Malaki…), kalidad (Basag…), o custom. "
      + "Ang mga grado lang na <strong>nabibili</strong> ang lumalabas sa pagtatala ng entry at sa mga "
      + "order; ang mga hindi nabibiling bucket ay para sa bookkeeping.",
    gradesDeactivating:
      "Hindi kailanman binubura ang mga grado. Ang <strong>Pag-deactivate</strong> ay nag-aalis ng isang "
      + "grado sa pagtatala at sa mga picker ng order: patuloy na binibilang ang stock nito at puwede pa "
      + "ring kumpirmahin ang mga linya ng order na naidagdag na noon, pero hindi na ito puwedeng ilagay sa "
      + "<em>bagong</em> linya ng order — i-reactivate ang grado para ibenta ang natitirang stock. Patuloy "
      + "na ipinapakita ng history ang pangalan nito.",
    gradesAdminOnly: "Configuration ang katalogo ng grado — ang pamamahala nito ay para sa admin lang.",

    productsHeading: "Mga Produkto (admin)",
    productsWhatYouSell:
      "Ang mga produkto ang ibinebenta mo — \"Malalaking Itlog kada dosena\", \"Halong karton\". Ang "
      + "bawat produktong itlog ay tumuturo sa isang grado ng itlog (doon nanggagaling ang stock nito) at "
      + "may dalang selling unit at opsyonal na default na presyo. Mga produktong itlog lang ang mayroon "
      + "sa ngayon.",
    productsPackedUnits:
      "Ang <strong>mga packed unit</strong> ang nagtatakda kung ilang itlog ang laman ng bawat unit — "
      + "puwedeng 12, 18, o 30 ang karton mo. Ang pagbabago ng isang unit ay nakakaapekto lang sa mga "
      + "hinaharap na benta; pinapanatili ng mga lumang order ang bilang na ibinenta sa kanila.",

    stockHeading: "Stock",
    stockLots:
      "Ang bawat grado ay lumalawak sa <strong>mga lote</strong> nito (isa kada naisumiteng araw), at ang "
      + "bawat lote naman ay lumalawak sa <strong>talaan ng galaw</strong> nito — isang malinaw na linya "
      + "para sa bawat production, benta, pagtatama, o void. Palaging katumbas ng balanseng ipinapakita ang "
      + "tumatakbong kabuuan; walang nagbabago sa stock nang hindi nag-iiwan ng linya. Ipinapakita ng "
      + "listahan ng lote ang pinakabagong 50 sa bawat pagkakataon — paliitin ito gamit ang mga petsang "
      + "<strong>Mula/Hanggang</strong> para maabot ang mas lumang lote, o magpatuloy sa pag-load pa.",
    stockRestricted:
      "Ang stock ay ang kabuuan ng mga lote ng itlog mo kada grado. Ang column na "
      + "<strong>restricted</strong> ay nakalaan para sa mga withholding period ng gamot — darating ang "
      + "feature na iyon kasama ng medication tracking. <strong>Wala pang minamarkahang restricted na "
      + "itlog, kaya hindi ipinapatupad ng system ang mga withdrawal time sa ngayon</strong> — pamahalaan "
      + "ang mga withholding period sa labas ng Cluckwork sa ngayon.",
    stockFifo: "Palaging kinukuha muna ng pagbebenta ang mga pinakalumang lote, para natural na umiikot ang stock.",
    stockWriteOff:
      "Ang nawalang stock — nabasag, nasira, itlog na nagamit sa bahay — ay itinatala gamit ang "
      + "<strong>i-write off</strong> sa lote (Owner/Manager, kailangan ng dahilan). Binabawasan nito ang "
      + "available ng lote nang hindi ginagalaw ang mga bilang ng produksyon ng araw; maaari ring magbalik ng "
      + "itlog ang recount, hanggang sa dating na-write off. Kung sinasabi ng recount na mali ang "
      + "<em>pangingitlog</em> ng araw, ayusin na lang ang araw-araw na entry.",

    inventoryHeading: "Feed at Imbentaryo",
    inventoryItems:
      "Tinutukoy ng <strong>mga item</strong> kung ano ang tinatrack mo (feed, supplement…) at ang unit na "
      + "sinusukat dito. Naka-lock ang unit sa sandaling natanggap na ang stock — dapat manatiling ang ibig "
      + "sabihin ng mga nakatalang dami ang dating ibig sabihin nito.",
    inventoryPurchaseUsage:
      "Ang <strong>Itala ang pagbili</strong> ay nagtatala ng natanggap na stock bilang isang may petsang "
      + "lote na may kasamang gastos. Ang pagpapakain sa kawan ay itinatala sa <strong>pahina ng "
      + "Pakain</strong> — ang panel ng isang item na maipapakain ay direktang naka-link doon nang "
      + "paunang napili ang item.",
    inventoryLedger:
      "Napupunta ang bawat pagbabago sa <strong>talaan ng galaw</strong> ng item — pagbili, paggamit, "
      + "pagtatama. Hindi kailanman ine-edit o binubura ang mga row ng talaan.",
    inventoryCorrections:
      "Inaayos ang mga typo at pagkasira gamit ang <strong>mga pagtatama</strong>: isang <em>Adjustment</em> "
      + "(kahit anong direksyon) o isang <em>Discard</em> (write-off) laban sa isang tiyak na lote, laging "
      + "may dahilan. Nananatiling nakikita ang orihinal na row at ang pagtatama.",
    inventoryPermissions:
      "Bukas sa lahat ang pagtatala ng pagbili at paggamit; ang katalogo ng item at mga pagtatama ng "
      + "stock ay para sa admin lang.",

    feedHeading: "Pakain",
    feedRecording:
      "Ang <strong>Itala ang pakain</strong> ay nagtatala kung ano ang kinain ng kawan sa isang araw: "
      + "piliin ang kawan, ang item (makikita ang kasalukuyang stock sa mismong picker), ang dami, at ang "
      + "petsa. Ang stock ay kinukuha muna mula sa pinakalumang mga binili — mga lote lang na umiiral "
      + "noong petsang iyon — at ang tantiyang gastos ay mula sa mga aktwal na lote na nagamit. Nakalista "
      + "sa history ng pahina ang bawat pagpapakain kasama ang tantiyang gastos nito.",
    feedCorrecting:
      "Ang mga tala ng pakain ay <strong>hindi kailanman ine-edit</strong>: nasa ledger na ang stock na "
      + "kinuha nila, kaya ang maling entry ay itinutuwid sa pamamagitan ng <strong>adjustment</strong> sa "
      + "Inventory sa apektadong lote (may dahilan), na nananatiling nakikita katabi ng orihinal.",
    feedDailyEntry:
      "Ipinapakita ng pahina ng <strong>Daily Entry</strong> ang pakain at tubig ng napiling kawan at araw "
      + "sa isang sulyap, na may link dito. Ang tala ng pakain o tubig na ginawa habang umiiral na ang "
      + "entry ng araw na iyon ay naaalala rin ang entry na iyon — ang mga ginawa bago nito ay sadyang "
      + "hindi naka-link; ang araw mismo ang nag-uugnay sa kanila.",
    waterHeading: "Tubig",
    waterRecording:
      "Itala kung ano ang ininom ng bawat kawan kada araw: alinman sa direktang dami (litro o galon) o "
      + "<strong>mga reading ng metro</strong> — ang dami ay ang pagkakaiba ng metro (katapusan − simula).",
    waterCorrecting:
      "Walang stock sa likod ng mga rekord ng tubig, kaya inaayos ang mga pagkakamali sa pamamagitan ng "
      + "<strong>direktang pagtatama sa rekord</strong> (ang button na \"tama\", para sa admin lang) — "
      + "walang compensating entry. Naka-fix ang kawan at petsa: kung mali ang napili, itala ito ulit sa "
      + "tamang isa.",
    waterLifecycle:
      "Parehong panuntunan ng lifecycle gaya ng lahat ng lugar: tumatanggap ang mga naubos na kawan ng "
      + "backfill hanggang sa petsa ng pag-ubos nila, walang tinatanggap ang mga naka-archive.",

    salesHeading: "Mga Customer at Benta",
    salesCustomerEdit:
      "Puwedeng i-edit anumang oras ang detalye ng customer (pangalan, telepono, email, address, tala) "
      + "mula sa Customers page.",
    salesCustomerLink:
      "Ang pangalan ng customer sa Customers page at sa dashboard ay isang link papunta sa Sales, "
      + "naka-filter sa mga order ng customer na iyon.",
    salesDrafts:
      "Nagsisimula ang mga order bilang <strong>draft</strong>: magdagdag ng linya sa pamamagitan ng "
      + "pagpili ng <strong>produkto</strong>, isang packed unit (dosena, karton, …), isang buong bilang "
      + "na dami, at isang presyo kada unit (naka-prefill mula sa default ng produkto, pinapayagan ang "
      + "decimal) — i-edit nang malaya, o <strong>kanselahin</strong> (mananatili ang draft, read-only). "
      + "Ang dami ay bilang ng <strong>mga unit, hindi mga itlog</strong> — nakasaad sa field ang unit at "
      + "ipinapakita nito ang kabuuang itlog habang nagta-type (2 tray = 60 itlog, hindi 60 tray). "
      + "Naaalala ng bawat linya kung ilang itlog ang laman ng unit nito noong idinagdag ito, kaya hindi "
      + "kailanman binabago ng muling pagtukoy sa isang karton ang mga lumang order.",
    salesConfirming:
      "Ang <strong>Pagkumpirma</strong> ng isang order ay naglalaan ng aktwal na stock — pinakalumang lote "
      + "muna — at ito ang sandali kung saan lumilipat ang inventory.",
    salesVoiding:
      "Ang isang maling kumpirmasyon ay ina-undo gamit ang <strong>Void</strong> (admin lang, kailangan ng "
      + "dahilan): bumabalik ang mga itlog sa eksaktong lote na pinagmulan nila, at mananatiling nakalista "
      + "bilang Na-void ang order. Ang pag-void ay para sa mga pagkakamali, hindi para sa mga pagsauli ng "
      + "naihatid na paninda. (Ang mga order na nakumpirma bago pa magkaroon ng lot-level allocation "
      + "tracking ay hindi puwedeng i-void nang mag-isa — magtanong sa administrator mo.)",
    salesPayments:
      "<strong>Mga Bayad</strong> (Benta, Manager, o admin — ang pag-void ng isang bayad ay para sa "
      + "admin/manager lang): ipinapakita ng panel ng isang nakumpirmang order ang settlement history nito "
      + "— magtala ng bahagyang bayad (petsa, halaga, paraan, opsyonal na reference) hanggang umabot sa "
      + "zero ang natitirang halaga; tinatanggihan ang overpayment. Ang isang maling bayad ay "
      + "<strong>ina-void</strong> (kailangan ng dahilan) at tumataas muli ang natitirang halaga. Ang isang "
      + "order na may bayad ay hindi puwedeng i-void hangga't hindi pa na-void ang mga bayad nito. "
      + "Ipinapakita ng Customers page ang natitirang balanse ng bawat customer.",

    reportsHeading: "Mga Report",
    reportsProduction:
      "<strong>Production</strong> (lahat): pumili ng date range — itlog kada araw, nawala, naibibenta, "
      + "may depekto, namatay, at <strong>hen-day %</strong> (itlog na nakolekta ÷ ibong buhay sa araw na "
      + "iyon × 100), may kasamang period total at breakdown kada grado. Hindi binibilang ang draft at "
      + "na-void na entry.",
    // #396 — machine-drafted, pending native review (#182).
    reportsCondition:
      "<strong>May depekto</strong>: basag at maruming itlog na naging stock sa halip na ituring na "
      + "lugi. Binibilang ang mga ito sa <strong>Bilang ng itlog</strong>, hindi kailanman gine-grade sa "
      + "kamay, kaya wala ang mga ito sa <strong>Maibebenta</strong> — pagsamahin ang dalawa para makuha "
      + "ang lahat ng naibebenta sa araw na iyon. Magpapakita ng 0 kung naka-off ang mga gradong iyon sa "
      + "Settings, at ang mga araw na naitala bago mo ito i-on ay mananatiling 0 — ang pag-on ng grado ay "
      + "hindi kailanman binabago ang nakaraang araw.",
    reportsMoney:
      "<strong>Pera</strong> (admin): buod ng benta para sa mga order ng range (kita / nabayaran / "
      + "natitira), gastos kada kategorya, at <strong>basic profit</strong> — nakumpirmang kita bawas ang "
      + "nakatalang gastos, walang cost-of-goods.",
    reportsThrottle:
      "<strong>Kung tinanggihan ang report</strong>: iilan lang ang report na pinapatakbo ng farm nang "
      + "sabay, para hindi mapabagal ng isang abalang screen ang app para sa lahat. Ang paghingi ng marami "
      + "nang sabay — ilang tao na sabay-sabay magbukas ng Reports, o paulit-ulit na pagsubok — ay "
      + "maaaring sumagot ng <strong>subukan ulit maya-maya</strong> sa halip na talahanayan. Walang "
      + "naitala at walang nawala: pindutin ang <strong>subukan ulit</strong> sa Reports screen "
      + "maya-maya at muling tatakbo ito gamit ang parehong mga petsang pinili mo.",

    expensesHeading: "Mga Gastos (admin)",
    expensesRecording:
      "Itala ang lumalabas na pera: petsa, kategorya, deskripsyon, at halaga (sa currency ng bukid), "
      + "opsyonal na naka-link sa isang kawan. Ipinapakita ng month picker ang tumatakbong total; "
      + "pinapamahalaan ang mga kategorya sa parehong screen (ang pag-deactivate ng isa ay nagtatago nito "
      + "mula sa mga bagong gastos — pinapanatili ito ng mga naitala na).",
    expensesCorrections:
      "Ine-edit ng mga pagtatama ang gastos sa lugar nito (<strong>tama</strong> sa row). Kung may ibang "
      + "tao na nagtama nito muna, nire-reload ng form ang mga value nila at hinihiling sa iyong mag-apply "
      + "ulit. Hindi kailanman nagbabago ang currency na kinatalaan ng isang gastos.",
    expensesAdminOnly:
      "Data ng pera ang mga gastos, kaya ang buong screen — kasama ang pagtingin — ay para sa admin lang, "
      + "hindi tulad ng mga production screen kung saan nagtatala ang manggagawa.",

    historyHeading: "History",
    historyBrowse:
      "Tumingin sa mga naitalang araw-araw na entry, pinakabago muna, na-filter kada kawan at date range. "
      + "Ipinapakita ng status column ang buhay ng entry: Draft, Naisumite, Naka-lock (7+ araw na), "
      + "Na-adjust (i-hover para sa dahilan), o Na-void.",
    historyAdminActions:
      "Nagtatama ang mga admin mula rito: muling binubuksan ng <strong>i-adjust</strong> ang entry sa "
      + "parehong dalawang-hakbang na form ng Daily entry — parehong sellable na bilang, parehong grading "
      + "chip, parehong shortcut na <strong>ilagay lahat sa…</strong> — na may kailangang dahilan; "
      + "ina-undo ng <strong>i-void</strong> ang buong entry. Awtomatikong sumusunod ang stock at ang "
      + "talaan ng ibon.",
    historyDraftEdit:
      "May link na <strong>i-edit</strong> ang mga row na draft (lahat, hindi lang admin) na tumatalon "
      + "pabalik sa screen na Araw-araw na Tala na may kasamang kawan at araw na iyon — doon ine-edit ang "
      + "mga draft, hindi ina-adjust.",

    auditHeading: "Audit Log (admin)",
    auditLog:
      "Ang bawat corrective, destructive, o configuration na pagbabago ay napupunta sa audit log nang "
      + "awtomatiko: sino ang gumawa nito, kailan (UTC), ano ang naapektuhan, at ang dahilan kung mayroon. "
      + "Isinusulat ito kasabay ng pagbabago mismo — walang naiiwang bakas ang isang nabigong aksyon, "
      + "palaging may naiiwan ang isang matagumpay — at hindi kailanman ine-edit, ng kahit sino.",
    auditRecordTypeFilter:
      "Ang dropdown na \"Uri ng record\" sa audit log ay hindi ito ang nagsa-salà sa mga row — pinapaliit "
      + "lamang nito ang dropdown na \"Aksyon\" sa tabi nito para lang sa mga aksyong nangyayari sa uring "
      + "pinili mo (Kawan, Order ng benta, atbp.), para hindi mo na kailangang mag-scan sa isang mahabang "
      + "listahan ng bawat aksyong maaaring itala ng bukid. Piliin ang aksyon sa napaliit na listahang iyon "
      + "para talagang masalà ang mga row.",
    auditRecordHistory:
      "Ang Mga kawan, Grado ng itlog, Kasaysayan ng pang-araw-araw na entry, Benta at Gastos ay may "
      + "kolum na Kasaysayan na nagpapakita kung sino ang gumawa ng record at kailan, at kung sino ang "
      + "huling nagbago kung mayroon man. Galing ito sa parehong audit log, kaya walang kailangang dagdag "
      + "na pahintulot: kung nakikita mo ang record, nakikita mo ang kasaysayan nito.",
    auditRecordHistoryLink:
      "Buod lang ang column na iyon, hindi ang buong kwento. Ang Mga kawan, Grado ng itlog, Kasaysayan ng "
      + "pang-araw-araw na entry, Order ng benta, at Gastos ay may sariling link na \"Kasaysayan ng audit\" "
      + "sa kani-kanilang screen na nagbubukas ng buong kasaysayan para sa record na iyon — bawat corrective "
      + "o configuration na pagbabago, hindi lang ang paglikha at ang pinakabagong pagbabago — nang hindi na "
      + "kailangang hanapin sa global na Audit log sa itaas at i-filter mo mismo. Ang Lote ng itlog naman ay "
      + "may katulad na link na \"Kasaysayan ng pagsasaayos\" — mas makitid ito sa layunin: mga manual na "
      + "write-off at recount lang ang naitala laban sa lote mismo, kaya iyon ang ipinapakita, hindi ang "
      + "buong kwento ng lote.",
    auditRecordHistorySubmit:
      "Ang pag-save at pagsumite ng pang-araw-araw na entry ay iisang gawa, hindi pagbabago: kung ikaw "
      + "mismo ang gumawa ng dalawa, ipinapakita ka ng column na History bilang tagalikha at walang "
      + "nakalistang pagbabago. Kung iba ang nagsumite ng iyong draft, lalabas ang pagsumite nila bilang "
      + "huling pagbabago, kaya laging makikita kung sino ang nag-opisyal sa bilang ng araw na iyon. Ganito "
      + "rin ang pagkumpirma ng order ng benta. Sa alinmang paraan, ipinapakita ng column na History kung "
      + "KAILAN ito naisumite, dahil iyon ang sandaling pumasok sa stock ang mga itlog, at kung kailan "
      + "inilaan ang stock para sa order. Ang pagwawasto sa naka-lock na entry ay laging ipinapakita, "
      + "kahit ikaw ang gumawa nito, gayundin ang pagkansela ng sarili mong draft na order. Nakatago ang "
      + "pag-edit mo sa sarili mong draft bago isumite — bahagi lang iyon ng pagsulat nito. Ngunit kung iba "
      + "ang nag-edit ng draft mo, ipinapakita ang pagbabago nila, kaya hindi kailanman nawawala ang taong "
      + "naglagay ng mga bilang na pumasok sa stock. At mula noon, ipinapakita na rin ang sarili mong mga "
      + "susunod na pag-edit: kung ibinalik mo ang mga bilang sa gusto mo, ikaw ang pinapangalanan ng "
      + "column at ang oras na ginawa mo iyon, hindi ang pag-edit na pinalitan mo.",
    auditSystemActors:
      "May ilang tala na ang gumawa ay ang sistema, hindi isang tao, at nagpapakita ng pangalan sa "
      + "loob ng panaklong sa halip na email. Ang \"(bootstrap-admin)\" ay ang utos na gumawa ng "
      + "iyong kauna-unahang owner account, bago pa may sinumang makagawa nito. Ang \"(break-glass)\" "
      + "ay ang pang-emergency na utos na nagre-reset ng password kapag na-lock out ang lahat — "
      + "itinatala rin ng entry na iyon kung saang makina ito pinatakbo at ang ibinigay na dahilan, "
      + "kaya hindi kailanman anonimo ang isang emergency reset. Ang \"(suspend-account)\" at "
      + "\"(reactivate-account)\" ay mga utos na pinapatakbo ng operator upang i-offline ang buong bukid "
      + "at ibalik ito; itinatala rin ng mga entry na iyon ang makinang pinatakbuhan at ang ibinigay na dahilan. "
      + "Ang \"(provision-account)\" ay gumagawa ng bagong bukid at unang Owner nito bago makapag-sign in ang sinuman sa bukid na iyon. "
      + "Ang iba ay pinapangalanan ang taong gumawa.",
    auditRecordHistoryOlder:
      "Ang mga record na ginawa bago ito idagdag ay walang linyang \"ginawa ni\" — wala lang talagang "
      + "entry ng paggawa sa log para sa kanila, at walang iniimbentong bago pagkatapos. Kung nabago ang "
      + "ganoong record pagkatapos, ipinapakita pa rin ang pagbabagong iyon; blangko lang ang kolum na "
      + "Kasaysayan kapag wala talagang anuman.",

    exportHeading: "Export at Backup (admin)",
    exportCsv:
      "Ida-download ng Export screen ang data mo bilang mga CSV file na puwede mong buksan sa kahit anong "
      + "spreadsheet — isang dataset sa isang pagkakataon, o lahat nang sabay bilang isang zip (ang "
      + "<strong>buong backup</strong>, may kasamang manifest ng bilang ng row). Magtago ng kopya sa ligtas "
      + "na lugar ayon sa sarili mong schedule; darating ang mga automatic na scheduled backup sa susunod "
      + "na phase.",
    exportFormats:
      "Naglalaman ang mga column ng pera ng minor unit (sentimo) kasama ang currency — eksaktong halaga, "
      + "hindi display formatting. ISO ang mga petsa (YYYY-MM-DD), at UTC ang mga timestamp.",

    farmSettingsHeading: "Mga Setting ng Bukid (admin)",
    farmSettingsIntro:
      "Nasa <strong>Setup → Mga Setting ng Bukid</strong> ang pangalan ng bukid at ang apat na bagay na "
      + "nagdedesisyon kung paano babasahin ang lahat: <strong>time zone</strong>, <strong>locale</strong>, "
      + "<strong>currency</strong>, at <strong>unit system</strong>. Opsyonal ang unang araw ng linggo at ang "
      + "mga format ng petsa at oras — iwanan itong blangko at ang locale ang magdedesisyon. Agad na "
      + "kumikilos ang time zone sa lahat ng lugar sa sandaling ma-save ito; naitatala ang iba pa laban sa "
      + "bukid at magtatakda kung paano ipapakita ang mga halaga, petsa, at sukat kapag dumating na ang "
      + "formatting na iyon.",
    farmSettingsTimezone:
      "Ang <strong>time zone</strong> ang araw ng bukid. Ang bawat field na nagtatala ng <em>kung kailan "
      + "nangyari ang isang bagay</em> — araw-araw na entry, kawan, tubig, paggamit at pagbili ng feed, "
      + "gastos, order at bayad — ay bumubukas dito at tumatanggi lumampas dito, anuman ang araw sa "
      + "telepono o laptop na hawak mo, para hindi na makapag-alok ang isang device na nauuna sa bukid ng "
      + "petsang tatanggihan din naman ng pag-save. Ang mga petsang nakatakdang mangyari sa hinaharap ay "
      + "walang cap: ang <strong>expiry</strong> ng isang lote ng feed, at ang mga date range na ginagamit "
      + "mo para i-filter ang History at Tubig.",
    farmSettingsCurrency:
      "Naka-lock ang <strong>currency</strong> sa sandaling itala ng bukid ang una nitong halaga — isang "
      + "benta, isang bayad, isang gastos, isang produktong may presyo, o perang ginastos sa feed. "
      + "Ipinapakita ang field na naka-lock kasama ang dahilan sa halip na hayaan kang mag-type ng code na "
      + "tatanggihan naman. Hindi na muling pinepresyuhan ang anumang naitala na, kaya naman ito naka-lock.",
    farmSettingsLogo:
      "Pinapalitan ng <strong>logo</strong> ang Cluckwork mark sa sidebar para sa lahat sa bukid. PNG, "
      + "JPEG, o WebP, hanggang sa size limit na ipinapakita sa screen (2 MB bilang default) at 4096 pixel "
      + "kada gilid. Tinatanggihan ang mga animated na larawan sa halip na patagin ito. Isang muling "
      + "ginawang kopya ang na-save: tinatanggal ang mga detalye ng camera at lokasyon papasok — ang isang "
      + "larawang kinunan sa telepono ay may kasamang kung saan ito kinuha, at para sa isang bukid, iyon "
      + "ang address nito. Alisin ito at babalik ang sidebar sa Cluckwork mark.",
    farmSettingsDateTimeFormat:
      "Nag-aalok ang <strong>format ng petsa</strong> at <strong>format ng oras</strong> ng ilang karaniwang "
      + "pagpipilian sa isang dropdown — pumili at tapos na. Kailangan ng hindi nakalista? Piliin ang "
      + "<strong>Custom…</strong> para mag-type ng sarili mo. Bawat petsa sa isang listahan — mga "
      + "pang-araw-araw na entry, lote ng itlog, order, bayad, gastos, tala ng feed at tubig, ulat — ay "
      + "ipinapakita gamit ang format ng petsa (kung wala, ang sariling maikling anyo ng locale ng "
      + "farm), kaya buksan ang anumang listahan para makita ang custom value; palaging ligtas ang mga "
      + "preset. Wala pang nagpapakita ng oras gamit ang format ng oras, kaya hindi makikita ngayon "
      + "ang isang maling custom na oras.",
    farmSettingsSquareLogo:
      "Lumalabas ang logo sa sidebar nang halos kasingtaas ng isang linya ng teksto. Maganda ang tingin "
      + "doon ng simple at maigsing-crop na marka at ng <strong>malawak na wordmark</strong> — pinananatili "
      + "ng wordmark ang sarili nitong hugis at ginagamit ang pahalang na espasyo sa halip na maipit sa "
      + "isang parisukat. Ang hindi umuubra sa sukat na iyon ay ang detalyadong larawan o masikip na eksena: "
      + "lumiliit ito hanggang hindi na mabasa. Ilagay ang detalyadong larawan sa banner ng bukid, kung saan "
      + "ipinapakita ito nang buong sukat.",
    farmSettingsBanner:
      "Ang <strong>banner</strong> ay pangalawa, independiyenteng larawan na ipinapakita nang buong-laki sa "
      + "isang splash screen kaagad pagkatapos mag-log in, isang beses kada session — maganda rito ang "
      + "malawak o detalyadong larawan, hindi katulad ng maliit na logo sa sidebar sa itaas. May sarili "
      + "itong limitasyon sa laki (5 MB bilang default) at sariling presensya: puwedeng magkaroon ang "
      + "isang bukid ng logo, banner, pareho, o wala. Kung wala, nilalaktawan nang buo ang splash.",
    farmSettingsCountingUnit:
      "<strong>Yunit ng pagbilang sa Daily Entry</strong> ang nagtatakda kung magkano ang bilang ng mga "
      + "+/− na button ng entry screen para sa lahat sa bukid — isang itlog, o isang pack unit tulad ng "
      + "Tray (30 bawat tap). Ang mga yunit lang na may aktibong depinisyon ng itlog-bawat-yunit sa "
      + "Products screen ang mapipili, at maaari itong i-override ng bawat tao para sa sarili nila sa "
      + "kanilang Account screen.",
    farmSettingsWorkerSaleAllocation:
      "Kinokontrol ng <strong>Paglalaan ng benta ng manggagawa</strong> kung saang mga lote ng itlog maaaring "
      + "kumuha ng stock ang benta ng isang nakatalagang plain Worker kapag nagkukumpirma ng order: "
      + "nakatalagang kawan lang (default) o lahat ng kawan sa bukid. Ang mga kumpirmasyon ng Owner, Manager, "
      + "at Sales ay laging farm-wide, anuman ang setting na ito (hindi makakumpirma ng benta ang Read-only).",

    farmPaletteHeading: "Paleta ng Bukid",
    farmPaletteIntro:
      "Ang mga setting ng bukid ay nagpapahintulot sa isang admin na pumili ng accent color na ginagamit "
      + "sa buong app para sa lahat sa bukid: Talong, Kagubatan, Slate, o Terracotta. Naaaplay ang pinili "
      + "kapag nag-save ka, at nakikita ito ng lahat sa susunod na pag-load ng app nila."
      + " Naaalala rin ng bawat device ang paleta para sa bawat bukid, kaya maaaring lumabas ang sariling "
      + "kulay ng isang bukid sa sign-in screen bago pa mag-sign in ang sinuman: kapag binuksan mo ang link "
      + "ng bukid na iyon, o kapag iisang bukid lang ang naaalala ng device. Ang device na nakakaalala ng "
      + "ilang bukid ay nagpapakita ng default na kulay hangga't hindi ka nag-sign in, at inaalis ng "
      + "Kalimutan ang bukid na ito ang naaalalang kulay ng bukid na iyon kasama ang code nito.",
    farmPaletteLightNight:
      "Magkahiwalay at personal ang light mode at night mode. Pumipili ang bawat tao ng sarili nila gamit "
      + "ang toggle sa sidebar, sa bawat device, at hindi kailanman ino-override ito ng paleta ng bukid — "
      + "dinisenyo ang bawat paleta para gumana sa pareho.",

    accountHeading: "Ang Iyong Account",
    accountPassword:
      "Ang <strong>Palitan ang password</strong> ay nangangailangan ng kasalukuyan mo at nagsa-sign out sa "
      + "iba mong device — magagawa ito ng bawat role para sa sarili nila.",
    accountLanguage:
      "Ang <strong>Wika</strong> ay nagpapalit ng interface para lang sa iyo, agad-agad, sa bawat device "
      + "na pinag-sign in mo.",
    accountCountingUnit:
      "<strong>Yunit ng pagbilang sa Daily Entry</strong> — kung magkano ang bilang ng IYONG mga tap ng "
      + "+/−, na nag-o-override sa default ng bukid mula sa Settings. Pumili ng pack unit tulad ng Tray "
      + "para magbilang bawat tray, o sundin ang default ng bukid para awtomatikong sumunod sa iyo ang "
      + "isang susunod na pagbabago sa buong bukid.",

    installHeading: "Pag-install sa Telepono",
    installIntro:
      "Puwedeng idagdag ang Cluckwork sa home screen ng isang telepono o tablet, kung saan makukuha nito "
      + "ang sariling icon at magbubukas sa sariling window nang walang browser bar — mas maraming espasyo "
      + "para sa mga entry screen at mas mabilis na maabot sa kulungan. Parehong app pa rin ito, hindi "
      + "hiwalay na download, kaya walang kailangang i-update mula sa isang app store.",
    installSteps:
      "<strong>Android (Chrome):</strong> buksan ang menu at piliin ang <strong>I-install ang app</strong> o "
      + "<strong>Idagdag sa Home Screen</strong>. <strong>iPhone/iPad (Safari):</strong> i-tap ang "
      + "<strong>Share</strong>, pagkatapos ay <strong>Idagdag sa Home Screen</strong>.",
    installHttps:
      "Inaalok lang ang pag-install sa isang secure (<strong>https</strong>) address. Kung naaabot ng "
      + "bukid mo ang Cluckwork sa isang plain na <strong>http</strong>, hindi lang lalabas ang opsyon — "
      + "walang sira, at gumagana pa rin ang app nang eksakto tulad ng sa browser.",
    installOffline:
      "<strong>Hindi</strong> ginagawang offline ang app ng pag-install. Kailangan pa rin nito ng koneksyon "
      + "para mag-load at mag-save; ang mga screen lang ng app mismo ang naka-save sa device para mabilis "
      + "itong magsimula. Nakaplano pang trabaho ang pagtatala habang offline, hindi isang bagay na "
      + "binubuksan ng pag-install.",
    installNewVersion:
      "Kapag may binagong bersyon, makikita mo ang <strong>\"May bagong bersyon ng Cluckwork na handa "
      + "na\"</strong>. Hinihintay ka nito sa halip na mag-reload habang nagta-type ka — pindutin ang "
      + "<strong>I-reload</strong> kapag maganda ang timing mo, o <strong>Mamaya</strong> at magtatanong "
      + "ulit ito sa susunod. Walang mawawala sa pag-iwan nito.",

    mistakesHeading: "Pagtatama ng mga Pagkakamali",
    mistakesIntro:
      "Kailangan ng sign-in ng admin ang bawat ayos sa talahanayang ito (tingnan ang \"Sino ang puwedeng "
      + "gumawa ng ano\") — nagtatala ang manggagawa, nagtatama ang admin. Ang isang eksepsiyon: nagtatala "
      + "pa rin ang isang <em>draft</em>, hindi nagtatama, kaya ine-edit ng manggagawa ang sarili nilang "
      + "draft.",
    mistakesTableMistakeHeader: "Pagkakamali",
    mistakesTableFixHeader: "Ayos",

    mistakesRow1Mistake: "Naubos o na-archive ang maling kawan",
    mistakesRow1Fix: "Mga Kawan → <strong>Reactivate</strong> (buong ma-rereverse)",

    mistakesRow2Mistake: "Maling bilang ng ibon",
    mistakesRow2Fix: "Mga Kawan → talaan ng ibon → <strong>Adjustment</strong> (kahit anong direksyon)",

    mistakesRow3Mistake: "Nakumpirma ang maling order ng benta",
    mistakesRow3Fix:
      "Benta → buksan ang order → <strong>I-void ang order</strong> (babalik ang stock sa mga lote nito; "
      + "kailangan ng dahilan). Kung may naitalang bayad dito, i-void muna iyon.",

    mistakesRow4Mistake: "Naitalang maling bayad",
    mistakesRow4Fix:
      "Benta → buksan ang order → mga bayad → <strong>i-void</strong> (kailangan ng dahilan): mananatili "
      + "ang row at tataas muli ang natitirang halaga.",

    mistakesRow5Mistake: "Maling <em>dami</em> sa isang pagbili ng feed / nasirang feed",
    mistakesRow5Fix:
      "Imbentaryo → buksan ang item → <strong>I-tama ang stock</strong> (Adjustment o Discard laban sa "
      + "lote; kailangan ng dahilan). Mga dami lang ang puwedeng itama — hindi pa puwedeng ayusin ang "
      + "maling gastos, petsa, o numero ng lote, kaya siguraduhin muna ang mga iyon bago mag-save.",

    mistakesRow6Mistake: "Sobra o kulang ang naitalang paggamit ng feed",
    mistakesRow6Fix:
      "Parehong form ng pagtatama: ibinabalik ng isang positibong Adjustment sa lote ang labis na nagamit "
      + "na stock (hanggang sa natanggap nito); inaalis naman ng negatibong isa ang kulang na naitalang "
      + "stock. Nananatiling nakatala ang rekord ng paggamit mismo at ang tantiya ng gastos nito — "
      + "inaayos ng mga pagtatama ang stock, hindi ang history.",

    mistakesRow7Mistake: "Maling rekord ng tubig",
    mistakesRow7Fix:
      "Tubig → <strong>tama</strong> sa rekord — nae-edit sa lugar nito ang dami, source, metro, at tala "
      + "(walang stock sa likod ng tubig). Naka-fix ang kawan at petsa: kung mali ang napili, itala ito "
      + "ulit sa tamang isa.",

    mistakesRow8Mistake: "Maling numero sa isang <em>naisumiteng</em> araw-araw na entry",
    mistakesRow8Fix:
      "History → <strong>i-adjust</strong> (admin) — kabuuan, nawala, mortality, at hati ng grado, may "
      + "kasamang kinakailangang dahilan. Dapat eksaktong tumugma ang mga naitamang grado sa naitamang "
      + "naibibentang bilang, ang parehong panuntunan na ginagamit ng Isumite, at naka-block ang "
      + "<strong>I-save ang pag-adjust</strong> hangga't hindi pa tumutugma ang mga ito. Awtomatikong "
      + "tumutugma ang stock at ang talaan ng ibon, pero hindi na kailanman puwedeng bawasan ang mga itlog "
      + "na nabenta na: tinatanggihan ang pagbawas ng isang grado sa ibaba ng nabenta na. Nananatiling "
      + "nakikita ang mga naunang value sa entry.",

    mistakesRow9Mistake: "Ang buong <em>naisumiteng</em> entry ay mali (maling kawan o araw)",
    mistakesRow9Fix:
      "History → <strong>i-void</strong> (admin, kailangan ng dahilan): nawawalan ng laman ang mga lote ng "
      + "itlog nito, ibinabalik ang mga namatay sa talaan ng ibon, at nananatiling Na-void ang entry. "
      + "Tinatanggihan kung nabenta na ang alinman sa mga itlog nito — i-void muna ang benta. Pinapalaya "
      + "ng pag-void ang araw: puwede nang itala ang tamang entry para sa parehong kawan at petsa.",

    mistakesRow10Mistake: "Pagkakamali sa isang entry o order na <em>draft</em>",
    mistakesRow10Fix:
      "I-edit ito — nae-edit ang lahat: mga numero ng draft, linya ng grado, at linya ng order (mga draft "
      + "na entry: History → <strong>i-edit</strong> tumatalon sa screen na Araw-araw na Tala na may "
      + "kasamang araw). Naka-fix pa rin ang kawan/petsa ng isang entry at ang customer/petsa ng isang "
      + "order: kung mali ang napili, itala na lang ito ulit sa tamang isa (at kanselahin ang maling draft "
      + "na order).",

    // machine-drafted (#182) — pending native review. Task 33 (B6b): the
    // glossary table (37 rows) + closing repo-note. Keys and tags mirror
    // en.ts exactly — catalogParity enforces key-set and tag parity.
    // Domain nouns follow this pack's existing loanword precedent seen
    // above (Stock, Currency, FIFO, Packed unit, Adjustment, Discard,
    // Navigation, Cull, Mortality, Deplete, Archive kept English;
    // void/confirm/cancel/adjust/reactivate as the established
    // i-void/kumpirmahin/kanselahin/i-adjust/i-reactivate verb loanwords).
    // The "specs/product/GLOSSARY.md" path inside glossaryRepoNote's <code>
    // tag is kept literal (untranslated), per the Task 33 brief.
    glossaryHeading: "Glossary",

    glossaryNavigationTerm: "Navigation",
    glossaryNavigationDef:
      "Nasa sidebar sa kaliwa ang mga screen sa computer; sa telepono, ang apat na pinakamadalas mong "
      + "gamitin ay mga tab sa ibaba, at ang iba pa ay nasa ilalim ng More.",

    glossaryPageLoadingTerm: "Pag-load ng page",
    glossaryPageLoadingDef:
      "Ang maikling mensaheng ipinapakita habang binubuksan ng Cluckwork ang isang screen na hindi pa nalo-load. Magagamit pa rin ang navigation, at nawawala ang mensahe kapag handa na ang screen.",

    glossarySearchablePickerTerm: "Picker na may search",
    glossarySearchablePickerDef:
      "Ang search-habang-nagta-type na control na ginagamit sa mga field ng pangalan ng kawan at customer. "
      + "Ang pag-type ay nag-e-explore ng mga resulta nang hindi binabago ang kasalukuyan mong napili; "
      + "pindutin ang <strong>Enter</strong> o i-click ang isang resulta para piliin ito, o "
      + "<strong>Escape</strong> para kanselahin at panatilihin ang dating napili. Kumukuha ng karagdagang "
      + "resulta ang <strong>Mag-load ng higit pa</strong>, at inuulit ng <strong>Subukan ulit</strong> ang "
      + "isang search o load-more na nabigo. Ang naalala o naka-link na pangalang hindi na mahanap ay "
      + "ipinapakita bilang <strong>Hindi available</strong>.",

    glossaryOperationalDayTerm: "Araw ng operasyon",
    glossaryOperationalDayDef:
      "Ang petsa ay ang araw sa kalendaryo ng iyong bukid, batay sa sariling time zone ng bukid at hindi "
      + "sa relong nasa ibang lugar. Ito ang parehong \"ngayon\" saanman: kung ano ang bilang na petsang "
      + "darating kapag nagtatala ka ng trabaho, kailan aalis ang itlog sa withdrawal period, kung aling "
      + "itlog ang puwedeng kunin ng isang benta, ang araw na na-deplete o na-archive ang isang kawan, at "
      + "ang range na binubuksan ng mga report. Bawat field na nagtatala kung KAILAN NANGYARI ANG ISANG "
      + "BAGAY ay nagbubukas dito at hindi ito lalagpasan, kahit anong araw ang nasa device mo. Hindi "
      + "limitado ang mga petsang dapat mahulog sa hinaharap — ang expiry ng isang batch ng feed, at ang "
      + "mga filter ng History at Tubig.",

    glossaryInstallToHomeScreenTerm: "Pag-install sa home screen",
    glossaryInstallToHomeScreenDef:
      "Ang pagdagdag ng Cluckwork sa home screen ng telepono o tablet mula sa browser, para magkaroon ito "
      + "ng sariling icon at magbukas sa sarili nitong window nang walang mga bar ng browser. Iisa lang "
      + "itong app, hindi hiwalay na download — walang i-a-update mula sa isang app store. Naialok lang sa "
      + "pamamagitan ng secure (https) address, at <strong>hindi</strong> nito ginagawang offline ang app: "
      + "kailangan pa rin nito ng koneksyon para mag-load at mag-save.",

    glossaryNewVersionReadyTerm: "May bagong bersyon na",
    glossaryNewVersionReadyDef:
      "Pagkatapos ng release, napapansin ng naka-install na app ang bagong bersyon sa background at "
      + "nagtatanong bago lumipat, sa halip na mag-reload habang nagta-type ka. Pindutin ang I-reload "
      + "kapag maganda ang timing mo, o Mamaya at magtatanong ulit ito sa susunod. Walang mawawala sa "
      + "pag-iwan nito — patuloy na gumagana ang tumatakbong app hanggang tanggapin mo ito.",

    glossaryTooManyReportsTerm: "Masyadong maraming report nang sabay",
    glossaryTooManyReportsDef:
      "Iilan lang ang report na pinapatakbo ng farm nang sabay, para hindi mapabagal ng isang abalang "
      + "screen ang app para sa lahat. Kapag lumampas doon, sasagot ang report na subukan ulit maya-maya "
      + "sa halip na pumila. Walang naitatala at walang nawawala — pindutin ang subukan ulit sa Reports "
      + "screen maya-maya; muling tatakbo ito gamit ang parehong mga petsang pinili mo. May sariling "
      + "alokasyon ang bawat farm, kaya hindi kailanman nauubos ng report ng ibang farm ang sa iyo.",

    // #532 — the login screen asks for it before the email, because one
    // email can belong to several farms.
    // (machine-drafted, pending native review)
    glossaryFarmCodeTerm: "Code ng bukid",
    glossaryFarmCodeDef:
      "Ang maikling code na nagpapaalam sa bukid mo sa screen ng sign in. Isinusulat mo ito "
      + "bago ang iyong email, dahil maaaring nasa ilang bukid ang iisang email address at ang "
      + "code lamang ang nagsasabi kung alin ang ibig mo sabihin. Lowercase ito at hindi ito "
      + "nagbabago. Naaalala ng screen ng sign in ang huling 10 bukid na naka-sign in ka sa "
      + "device na ito, pinakabago muna, at inaalok ang bawat isa — kahit isang natatandaan "
      + "lang — sa isang pumipili para hindi mo na kailangang i-type ang code. Ang bawat "
      + "entry ay maaaring kalimutan nang hiwalay gamit ang kanyang kontrol na Kalimutan, pagkatapos "
      + "ng isang kumpirmasyon; ang aalis ay ang bukid na iyon lamang sa device na ito at "
      + "hindi binubura nito ang iyong wika o theme o ibang preference. Ang link tulad ng "
      + "/login?farm=<code> ang mananaig at awtomatikong pinupuno ang code para sa iyo, nang "
      + "hindi ipinapakita ang listahan. Kung hindi mo alam ang code ng bukid mo, tanungin "
      + "ang iyong administrator: matitingnan nila ito.",

    glossaryLoginEmailTerm: "Email sa pag-sign in",
    glossaryLoginEmailDef:
      "Ang address na ginagamit kasama ng code ng bukid para mag-sign in. Maaari itong palitan agad ng Owner "
      + "sa Users screen; hindi na gagana ang lumang address, matatapos ang mga bukas na session, at walang "
      + "ipinapadalang confirmation email.",

    // #533 (machine-drafted, pending native review)
    glossaryFarmProvisioningTerm: "Pag-provision ng bukid",
    glossaryFarmProvisioningDef:
      "Ang command para lamang sa operator na gumagawa ng bagong bukid, ng mga default na egg grade at "
      + "conversion ng naka-pack na unit nito, at ng una nitong Owner sa iisang transaksyon. "
      + "Nagsisimula sa UTC ang bukid; pagkatapos palitan ng Owner ang isang-gamit na password sa unang "
      + "pag-sign in, pipiliin nila ang IANA timezone ng bukid sa Settings. "
      + "Tumatakbo ito sa labas ng app; hindi ito pinapatakbo ng mga user ng bukid.",

    glossaryTooManySignInAttemptsTerm: "Masyadong maraming pagtatangkang mag-sign in",
    glossaryTooManySignInAttemptsDef:
      "Rate-limited ang pag-sign in para pabagalin ang panghuhula ng password: masyadong maraming "
      + "pagtatangka mula sa iisang lugar sa loob ng ilang minuto ay tatanggihan gamit ang mensaheng ito "
      + "hanggang lumipas ang maikling paghihintay. Hindi nito naaapektuhan ang session na naka-sign in "
      + "na.",

    // #532 (machine-drafted, pending native review)
    glossaryForcedReauthTerm: "Ilang bukid sa isang browser",
    glossaryForcedReauthDef:
      "May hiwalay na ligtas na session cookie ang bawat bukid, kaya hindi maaaring palitan o burahin ng isang "
      + "bukid ang session ng iba. Naaalala ng tab ang napili nitong bukid kapag nag-reload. Kapag nakakita ang "
      + "tab na walang natatandaang bukid ng ilang session, babalik ito sa pag-sign in sa halip na "
      + "manghula; piliin ang code ng bukid na gusto mo.",

    // #308/#356/#360 (machine-drafted, pending native review)
    glossaryStepUpAuthTerm: "Karagdagang pagpapatunay (step-up)",
    glossaryStepUpAuthDef:
      "Isang karagdagang tsek bukod sa pagiging naka-sign in: bago gumawa ng kahit anong user, mag-reset ng "
      + "password ng kahit sinong user, magpalit ng tungkulin ng kahit sinong user, magpalit ng email sa pag-sign in, "
      + "mag-disable ng user, muling mag-enable ng user, mag-assign ng manggagawa sa isang kawan, o mag-alis ng "
      + "assignment ng manggagawa sa isang kawan, hinihiling ng Users screen na muling ilagay ang kasalukuyan "
      + "mong password mismo sa dialog. Kinukumpirma nito na ikaw talaga bago magbigay — o mag-alis — ng access. "
      + "Hindi muling hinihingi ito sa mga pagbabago ng display name.",
    glossarySomethingWentWrongScreenTerm: "Screen na \"Something went wrong\"",
    glossarySomethingWentWrongScreenDef:
      "Ipinapakita ito ng isang screen kapag may error, sa halip na maging blangko. Ligtas ang naka-save "
      + "na data — maaaring kailanganin mong muling i-type ang anumang tina-type mo pa; i-tap ang "
      + "I-reload o Bumalik sa dashboard. Naglalaman ang \"Error details\" ng mensahe para sa screenshot.",

    glossaryDailyEntryTerm: "Araw-araw na Tala",
    glossaryDailyEntryDef: "Ang araw ng isang kawan: itlog ayon sa grado, nawala, namatay. Ang pag-grade nang lampas sa kabuuan ay nagpapataas nito para tumugma. Draft hanggang isumite.",

    glossaryEggLotTerm: "Lote ng itlog",
    glossaryEggLotDef:
      "Isang may petsang batch ng maibebentang itlog ng isang grado, ginawa sa pamamagitan ng pagsumite "
      + "ng entry. Ang stock ay ang kabuuan ng mga lote.",

    glossaryGradeTerm: "Grado",
    glossaryGradeDef: "Isang grading bucket (sukat, kalidad, o custom). Ang mga grado na nabibili ay puwedeng ibenta.",

    glossaryEggMovementLedgerTerm: "Talaan ng galaw ng itlog",
    glossaryEggMovementLedgerDef:
      "Ang history line by line sa likod ng balanse ng isang lote ng itlog: papasok na production, "
      + "palabas na benta, at mga pagtatama at void na may kaukulang sign.",

    glossaryStockWriteOffTerm: "Write-off ng stock",
    glossaryStockWriteOffDef:
      "Pagwawasto ng Owner/Manager na nag-aalis ng nawalang itlog mula sa isang lote (nabasag, nasira, nagamit "
      + "sa bahay) o naglalapat ng recount, na may kinakailangang dahilan. Binabago lamang nito ang available ng "
      + "lote — hindi ginagalaw ang mga bilang ng produksyon ng araw. Maaaring magbalik ng itlog ang recount "
      + "hanggang sa dating na-write off.",
    glossaryFifoTerm: "FIFO",
    glossaryFifoDef:
      "\"First in, first out\" — palaging kinukuha muna ng benta at ng paggamit ng feed ang pinakalumang "
      + "stock.",
    glossaryWorkerSaleAllocationTerm: "Paglalaan ng benta ng manggagawa",
    glossaryWorkerSaleAllocationDef:
      "Ang setting ng bukid na nagpapasya kung ang kumpirmasyon ng benta ng isang nakatalagang plain Worker "
      + "ay kukuha lang mula sa kanilang nakatalagang kawan (default) o mula sa buong bukid.",

    glossaryCullTerm: "Cull",
    glossaryCullDef: "Mga ibong sadyang inalis sa isang kawan (naibenta, pinatay, ibinigay) — hindi kamatayan.",

    glossaryMortalityTerm: "Mortality",
    glossaryMortalityDef:
      "Mga kamatayan, itinatala sa araw-araw na entry; awtomatikong napupunta sa talaan ng ibon kapag "
      + "isinumite.",

    glossaryDepleteTerm: "Deplete",
    glossaryDepleteDef:
      "Markahan ang isang kawan na wala nang natitirang ibon. Nananatili ang history; ma-rereverse gamit "
      + "ang Reactivate.",

    glossaryArchiveTerm: "Archive",
    glossaryArchiveDef: "Itago ang tapos na kawan mula sa araw-araw na trabaho. Ma-rereverse gamit ang Reactivate.",

    glossaryWithdrawalRestrictionTerm: "Withdrawal restriction",
    glossaryWithdrawalRestrictionDef:
      "Isang hold sa mga itlog habang may withholding period ng gamot. Darating kasabay ng medication "
      + "tracking — wala pang naglalagay ng restriction sa ngayon, kaya pamahalaan muna ang withholding "
      + "period sa labas ng Cluckwork.",

    glossaryProductTerm: "Produkto",
    glossaryProductDef:
      "Ang ibinebenta mo — tumuturo ang isang produktong itlog sa isang grado (ang pinagmumulan ng stock "
      + "nito) at may dalang selling unit at default na presyo.",

    glossaryPackedUnitTerm: "Packed unit",
    glossaryPackedUnitDef:
      "Ilang itlog ang laman ng isang dosena/tray/karton/case sa bukid mo. Iniingatan ng bawat linya ng "
      + "benta ang bilang na ipinagbenta dito.",
    glossaryCountingUnitTerm: "Yunit ng pagbilang",
    glossaryCountingUnitDef:
      "Kung magkano ang bilang ng bawat tap ng mga − / + na button ng Daily Entry — isang itlog, o isang "
      + "packed unit tulad ng tray. Default ng bukid sa Settings; ang sarili mong pili sa iyong Account "
      + "screen. Ipinapakita ng mga button ang halaga (−30 / +30) kapag hindi ito isa.",

    glossarySalesLineTerm: "Linya ng benta",
    glossarySalesLineDef:
      "Isang produkto sa isang order: isang buong bilang na dami sa selling unit, may presyo kada unit "
      + "(maaaring may decimal ang presyo); ang mga itlog sa likod nito ay dami × ang bilang ng itlog ng "
      + "unit.",

    glossaryConfirmOrderTerm: "Kumpirmahin (order)",
    glossaryConfirmOrderDef:
      "Ginagawang tunay na benta ang isang draft order at inilalaan ang stock. Ma-a-undo lang sa "
      + "pamamagitan ng pag-void.",

    glossaryVoidOrderTerm: "I-void (order)",
    glossaryVoidOrderDef:
      "Pag-undo ng isang maling kumpirmasyon — babalik ang stock sa eksaktong lote na pinagmulan nito. "
      + "Kailangan ng dahilan.",

    glossaryCancelOrderTerm: "Kanselahin (order)",
    glossaryCancelOrderDef: "Isara ang isang draft na hindi natuloy. Walang kinalaman na stock.",

    glossaryInventoryItemTerm: "Item sa imbentaryo",
    glossaryInventoryItemDef:
      "Isang catalog entry para sa isang bagay na nasa stock mo (feed, supplement…), na may fixed na unit "
      + "of measure.",

    glossaryInventoryLotTerm: "Lote ng imbentaryo",
    glossaryInventoryLotDef:
      "Isang natanggap na batch ng isang item, may sariling cost. On-hand = kabuuan ng mga lote.",

    glossaryInventoryMovementLedgerTerm: "Talaan ng galaw ng imbentaryo",
    glossaryInventoryMovementLedgerDef:
      "Ang append-only na trail ng bawat pagbabago sa stock ng feed/supply. Ang mga pagtatama ay bagong "
      + "row, hindi kailanman edit.",

    glossaryWaterUsageTerm: "Paggamit ng tubig",
    glossaryWaterUsageDef:
      "Ang ininom ng isang kawan sa isang araw — direktang dami o meter delta. Editable sa lugar; naka-fix "
      + "ang kawan/petsa.",

    glossaryFeedUsageTerm: "Paggamit ng feed",
    glossaryFeedUsageDef:
      "Ang kinain ng isang kawan sa isang araw; ina-drain ang mga lote nang FIFO at tinatantiya ang cost "
      + "mula rito.",

    glossaryAdjustmentDiscardTerm: "Adjustment / Discard",
    glossaryAdjustmentDiscardDef:
      "Mga pagtatama sa stock laban sa isang lote, kailangan ng dahilan. Discard = write-off (spoilage).",

    glossaryRolesTerm: "Mga Tungkulin",
    glossaryRolesDef:
      "Admin (may-ari), Manager, Manggagawa, Benta, Read-only — tingnan ang \"Sino ang puwedeng gumawa ng "
      + "ano\". Nagtatala ang mga manggagawa; nagtatama at nagko-configure rin ang mga manager; "
      + "hinahawakan ng benta ang mga order at bayad; nanonood lang ang read-only.",

    glossaryFlockScopingTerm: "Saklaw ng Kawan",
    glossaryFlockScopingDef:
      "Ang mga nababasa ng isang Manggagawa ay limitado sa mga itinalagang kawan nila at sa mga row ng "
      + "buong bukid. Walang limitasyon ang May-ari at Manager. Ang mga manggagawang walang row ng "
      + "pagtatalaga, o may row ng buong bukid, ay walang limitasyon rin. Ang detalye ng hindi itinalagang "
      + "kawan ay nagbabalik ng 404. Ang paggawa ng kawan ay laging para sa May-ari/Manager, anuman ang "
      + "saklaw.",

    glossaryLockedEntryTerm: "Naka-lock (entry)",
    glossaryLockedEntryDef:
      "Isang naisumiteng entry na mas matanda sa 7 araw — sarado sa mga karaniwang edit; gumagana pa rin "
      + "ang pag-adjust/void ng admin.",

    glossaryAdjustEntryTerm: "I-adjust (entry)",
    glossaryAdjustEntryDef:
      "Pagtatama ng admin sa isang naisumiteng entry. Dapat eksaktong tumugma ang mga naitamang grado sa "
      + "naitamang naibibentang bilang, ang parehong panuntunan na ginagamit ng Isumite — walang draft "
      + "state ang isang pagsasaayos na maiiwan itong bahagyang na-grade. Awtomatikong nagtutugma ang "
      + "stock at ang talaan ng ibon; hindi na magagalaw ang mga naibentang itlog; nananatiling nakikita "
      + "ang mga naunang value.",

    glossaryVoidEntryTerm: "I-void (entry)",
    glossaryVoidEntryDef:
      "Pag-undo ng admin sa buong naisumiteng entry — nauubusan ang mga lote, nire-reverse ang mga "
      + "kamatayan, at nananatiling Na-void ang entry. Tinatanggihan kapag naibenta na ang mga itlog nito.",

    glossaryFarmSettingsTerm: "Mga Setting ng Bukid",
    glossaryFarmSettingsDef:
      "Ang pangalan ng bukid, time zone, locale, currency, at unit system, kasama ang opsyonal na unang "
      + "araw ng linggo at mga format ng petsa/oras — pinipili mula sa dropdown ng mga preset, o "
      + "ini-type bilang custom na .NET format string. Setup → Mga Setting ng Bukid; nag-e-edit ang mga "
      + "may-ari at manager, nakakabasa ang lahat — hindi permission ang pag-format ng pera at petsa.",

    glossaryCurrencyLockTerm: "Currency lock",
    glossaryCurrencyLockDef:
      "Hindi na ma-e-edit ang currency ng bukid sa sandaling may magtala ng halaga dito — isang benta, "
      + "isang bayad, isang gastos, isang produktong may presyo, pera na ginastos sa feed. Ipinapakita ng "
      + "field na naka-lock ito kasama ang dahilan. Wala nang muling pinepresyuhan ang anumang naitala "
      + "na, ito mismo ang buong punto.",

    glossaryFarmLogoTerm: "Logo ng Bukid",
    glossaryFarmLogoDef:
      "Ang sarili mong larawan sa halip ng Cluckwork mark sa sidebar, ina-upload mula sa Mga Setting ng "
      + "Bukid. PNG, JPEG, o WebP (2 MB bilang default), still images lang; mas mabasa ang simpleng mark o "
      + "malawak na wordmark sa sukat na iyon, samantalang ang detalyadong larawan ay para sa banner ng "
      + "bukid. Naka-store bilang muling ginawang kopya na tinanggalan ng detalye ng camera at lokasyon.",

    glossaryFarmBannerTerm: "Banner ng Bukid",
    glossaryFarmBannerDef:
      "Pangalawa, independiyenteng larawan na ipinapakita nang buong-laki sa isang splash screen kaagad "
      + "pagkatapos mag-log in, isang beses kada session. Hiwalay sa logo ng bukid sa itaas — puwedeng "
      + "magkaroon ang isang bukid ng logo, banner, pareho, o wala. Parehong panuntunan ng still image "
      + "na PNG/JPEG/WebP, may sarili itong mas malaking limitasyon sa laki (5 MB bilang default), "
      + "ina-upload mula sa Mga Setting ng Bukid.",

    glossaryFarmPaletteTerm: "Paleta ng Bukid",
    glossaryFarmPaletteDef:
      "Ang accent color para sa buong bukid, pinipili ng isang admin sa Mga Setting ng Bukid. Hiwalay ito "
      + "sa sariling setting ng light/night mode ng bawat tao."
      + " Naaalala ito bawat bukid sa bawat device, kaya maaari itong lumabas sa sign-in screen bago "
      + "mag-sign in ang sinuman; ang device na nakakaalala ng ilang bukid ay nagpapakita ng default "
      + "hanggang sa mag-sign in.",

    glossaryUiLanguageTerm: "Wika ng UI",
    glossaryUiLanguageDef:
      "Ang wikang ipinapakita sa interface, kada user — English, Español, o Tagalog — pinipili mula sa "
      + "Account → Preferences. Ang English ang fallback para sa anumang screen na hindi pa naisasalin, "
      + "kahit anong wika ang pinili mo.",

    // #356 — appended last so it doesn't reshuffle the rows above.
    // (machine-drafted, pending native review)
    glossaryDisabledUserTerm: "Na-disable na user",
    glossaryDisabledUserDef:
      "Binawi ang access, hindi pagbura. Hindi makaka-sign in, makaka-refresh, o makakakuha ng step-up "
      + "grant ang isang na-disable na user, at natatapos ang bawat bukas niyang session sa susunod nitong "
      + "request. Ibinabalik ng muling pag-enable ang sign-in pero hindi ang mga lumang session na iyon — "
      + "kahit anong ibinigay bago ang pag-disable ay hindi na kailanman gagana. Hindi puwedeng i-disable "
      + "ang huling aktibong Admin (may-ari) ng account, at walang puwedeng mag-disable sa sarili niya.",

    glossaryRepoNote:
      "Nasa <code>specs/product/GLOSSARY.md</code> ng repository ang kumpletong mga depinisyon sa "
      + "spec-language.",
  },
} as const;
