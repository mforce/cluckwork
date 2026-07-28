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
