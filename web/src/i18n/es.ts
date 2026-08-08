// MACHINE-DRAFTED translation, PENDING NATIVE-SPEAKER REVIEW (#182 / epic #15). Keys mirror en.ts exactly.
//
// Translator note: UI screen/label names referenced in prose (e.g. "(Customers
// page)" in sales.addCustomerFirst) are kept in English, matching the actual
// on-screen label, until that screen itself is externalized to the catalog (#182).
export const es = {
  common: {
    cancel: "Cancelar",
    save: "Guardar",
    close: "Cerrar",
    delete: "Eliminar",
    edit: "Editar",
    add: "Agregar",
    confirm: "Confirmar",
    loading: "Cargando…",
    working: "Procesando…",
    workingHint:
      "Un botón con un indicador giratorio significa que el guardado sigue en curso — volver a pulsarlo no registrará lo mismo dos veces.",
    retry: "reintentar",
    required: "Requerido",
    optional: "Opcional",
    actions: "Acciones",
    search: "Buscar",
    all: "Todos",
    none: "Ninguno",
    yes: "Sí",
    no: "No",
  },
  auth: {
    title: "Cluckwork",
    email: "Correo electrónico",
    password: "Contraseña",
    signIn: "Iniciar sesión",
    signingIn: "Iniciando sesión…",
    invalidCredentials: "Correo electrónico o contraseña inválidos.",
    credentialsSuperseded: "Sus credenciales cambiaron. Inicie sesión de nuevo.",
    accountDisabled: "Su cuenta ha sido deshabilitada.",
    tooManyAttempts:
      "Demasiados intentos de inicio de sesión. Espere unos minutos y vuelva a intentarlo.",
    apiDown: "No se pudo iniciar sesión. ¿Está la API en ejecución?",
    // machine-drafted (#182) — pending native review.
    credentialsTooLong: "Eso es demasiado largo — revise su correo electrónico y contraseña.",
    noAdminYet:
      "Todavía no hay administrador. Esta granja no ha completado la "
      + "configuración inicial, así que no hay una cuenta de administrador con "
      + "la que iniciar sesión.",
    noAdminYetHint:
      "Pida a quien configuró este servidor que cree el primer administrador. "
      + "Los pasos de configuración están en el README del proyecto.",
    // machine-drafted (#283) — pending native review.
    setPasswordHeading: "Establezca su contraseña",
    setPasswordHint:
      "Este es su primer inicio de sesión. Establezca una nueva contraseña para "
      + "continuar — la contraseña temporal no volverá a funcionar después de esto.",
    temporaryPasswordLabel: "Contraseña temporal",
    setPasswordNewLabel: "Nueva contraseña (mín. {{min}} caracteres)",
    setPasswordConfirmLabel: "Confirmar nueva contraseña",
    setPasswordButton: "Establecer contraseña",
    setPasswordSubmitting: "Estableciendo contraseña…",
    setPasswordSignOut: "Cerrar sesión",
    setPasswordMismatchError: "Las nuevas contraseñas no coinciden.",
    setPasswordTooShortError: "La nueva contraseña debe tener al menos {{min}} caracteres.",
  },
  account: {
    preferences: "Preferencias",
    language: "Idioma",
    languageHint: "El idioma en que se muestra la interfaz, solo para usted.",
    stepperUnit: "Unidad de conteo de la entrada diaria",
    stepperUnitHint:
      "Cuánto cuentan los botones +/− de la entrada diaria, solo para usted — elija una "
      + "unidad de empaque como Tray para contar por bandeja en lugar de por huevo, o siga "
      + "el valor predeterminado de la granja.",
    stepperUnitFarmDefaultOption: "Predeterminado de la granja ({{unit}})",
    stepperUnitSaveFailed: "No se pudo guardar — su unidad de conteo no cambió.",

    // machine-drafted (#182) — pending native review. Task 25 (B4): the rest
    // of AccountPage. Keys mirror en.ts exactly, including {{role}}/{{min}}
    // placeholders and the <strong> tag in roleLine.
    heading: "Cuenta",
    roleLine: "Ha iniciado sesión con el rol de <strong>{{role}}</strong>.",
    changePasswordHeading: "Cambiar contraseña",
    changePasswordHint:
      "Al cambiar su contraseña se cerrará la sesión en todos los demás "
      + "dispositivos — este dispositivo seguirá con la sesión iniciada.",
    currentPasswordLabel: "Contraseña actual *",
    newPasswordLabel: "Nueva contraseña (mín. {{min}} caracteres) *",
    confirmPasswordLabel: "Confirmar nueva contraseña *",
    changePasswordButton: "Cambiar contraseña",
    passwordMismatchError: "Las nuevas contraseñas no coinciden.",
    passwordTooShortError: "La nueva contraseña debe tener al menos {{min}} caracteres.",
    passwordChangedMessage: "Contraseña cambiada. Se cerró la sesión en los demás dispositivos.",
  },
  errors: {
    "Me.Language.Format": "El idioma debe ser un código de 2 a 8 letras, por ejemplo 'en'.",
  },

  // machine-drafted (#182) — pending native review. Task CT1 (B1 catch-up):
  // backfilling es for the shared nav chrome (previously English-only under
  // the now-dropped English-first policy). Keys mirror en.ts nav exactly.
  // "groupYou" ("You") translated literally as "Usted" (formal register,
  // matching account.* elsewhere) — flag for native review, an unusual
  // section-heading rendering worth a second look.
  nav: {
    // Section headings (NavGroup.labelKey).
    groupOverview: "Resumen",
    groupProduction: "Producción",
    groupSalesStock: "Ventas y existencias",
    groupInsights: "Estadísticas",
    groupSetup: "Configuración",
    groupYou: "Usted",
    groupHelp: "Ayuda",

    // Destination labels (NavEntry.labelKey).
    dashboard: "Panel",
    dailyEntry: "Registro diario",
    flocks: "Lotes",
    feed: "Alimento",
    water: "Agua",
    inventory: "Inventario",
    stock: "Existencias",
    customers: "Clientes",
    sales: "Ventas",
    history: "Historial",
    reports: "Informes",
    expenses: "Gastos",
    farmSettings: "Configuración de la granja",
    grades: "Grados",
    products: "Productos",
    users: "Usuarios",
    audit: "Auditoría",
    export: "Exportación",
    account: "Cuenta",
    // Distinct key from groupHelp above, same coincidental-equal-text
    // treatment as en.ts's own comment describes.
    help: "Ayuda",

    // AppLayout chrome.
    skipToContent: "Saltar al contenido principal",
    primaryNavAriaLabel: "Principal",
    signOut: "Cerrar sesión",
    versionLabel: "v{{version}}",
    farmLoadFailedNeverLoaded:
      "No se pudo cargar la configuración de esta granja, así que las "
      + "fechas siguen a este dispositivo en lugar de a la granja.",
    farmLoadFailedStale:
      "No se pudo volver a leer la configuración de esta granja, así que "
      + "lo que ve aquí podría estar desactualizado.",
    tryAgain: "Reintentar",
    titleSuffix: " — Cluckwork",

    // BottomNav chrome.
    tabBarAriaLabel: "Secciones",
    moreButton: "Más",
    menuTitle: "Menú",
    allSectionsAriaLabel: "Todas las secciones",
  },

  // machine-drafted (#182) — pending native review. Task CT1 (B1 catch-up):
  // backfilling es for NumberField's stepper buttons. {{label}} is the
  // caller-supplied field name, interpolated not translated.
  numberField: {
    increaseLabel: "Aumentar {{label}}",
    decreaseLabel: "Disminuir {{label}}",
    increaseByLabel: "Aumentar {{label}} en {{step}}",
    decreaseByLabel: "Disminuir {{label}} en {{step}}",
  },

  // machine-drafted (#182) — pending native review. Task CT1 (B1 catch-up):
  // backfilling es for the app/screen error-boundary fallback UI.
  errorBoundary: {
    title: "Algo salió mal",
    screenBody:
      "Esta pantalla tuvo un problema y no pudo terminar de cargar. Todo lo "
      + "que ya había guardado está a salvo, pero es posible que tenga que "
      + "volver a escribir lo que todavía estaba escribiendo aquí. El resto "
      + "de la aplicación sigue funcionando.",
    appBody:
      "La aplicación tuvo un problema y no pudo terminar de cargar. "
      + "Recargar suele solucionarlo.",
    reload: "Recargar",
    backToDashboard: "Volver al panel",
    detailsSummary: "Detalles del error",
  },

  // machine-drafted (#182) — pending native review. Task CT1 (B1 catch-up):
  // backfilling es for the light/night mode toggle.
  themeToggle: {
    switchToLightMode: "Cambiar al modo claro",
    switchToNightMode: "Cambiar al modo nocturno",
    light: "Claro",
    night: "Nocturno",
  },

  // machine-drafted (#182) — pending native review. Task CT1 (B1 catch-up):
  // backfilling es for the shared useConfirm dialog's reason field. "Motivo"
  // matches the existing sales.voidReasonLabel precedent.
  useConfirm: {
    reasonLabel: "Motivo *",
    reasonRequired: "Se requiere un motivo.",
  },

  // machine-drafted (#182) — pending native review. Task CT1 (B1 catch-up):
  // backfilling es for the service-worker "update ready" banner
  // (UpdatePrompt.tsx, src/pwa).
  pwa: {
    updateAvailable: "Hay una nueva versión de Cluckwork lista.",
    reload: "Recargar",
    reloading: "Recargando…",
    later: "Más tarde",
  },

  sales: {
    // Headings
    title: "Ventas",
    loading: "Cargando…",
    payments: "Pagos",
    ordersHeading: "Pedidos",

    // Buttons
    newOrder: "Nuevo pedido",
    newDraftOrder: "Nuevo pedido borrador",
    save: "guardar",
    cancelEdit: "cancelar",
    edit: "editar",
    remove: "eliminar",
    addLine: "Agregar línea",
    confirmOrderButton: "Confirmar pedido (asigna existencias)",
    cancelDraft: "Cancelar borrador",
    // Intentional screen-specific lowercase variant, distinct from
    // common.close ("Cerrar") — mirrors en.sales.close (#182).
    close: "cerrar",
    voidPaymentButton: "anular",
    recordPayment: "Registrar pago",
    voidOrderButton: "Anular pedido (devuelve existencias)",
    open: "abrir",
    loadMore: "cargar más",

    // Form labels
    customer: "Cliente",
    date: "Fecha",
    product: "Producto",
    perLabel: "Por",
    quantity: "Cantidad",
    // #445
    quantityWithUnit: "Cantidad ({{unit}})",
    equalsEggs: "= {{count}} huevos",
    equalsEggs_one: "= {{count}} huevo",
    productOptionWithUnit: "{{name}} ({{count}} huevos/{{unit}})",
    productOptionWithUnit_one: "{{name}} ({{count}} huevo/{{unit}})",
    unitPriceWithCurrency: "Precio unitario ({{code}})",
    method: "Método",
    referenceOptional: "Referencia (opcional)",
    noteOptional: "Nota (opcional)",
    amountWithCurrency: "Monto ({{code}})",
    status: "Estado",

    // Table headers (shared across the items / payments / orders tables)
    qty: "Cant.",
    eggs: "Huevos",
    unitPrice: "Precio unitario",
    lineTotal: "Total de línea",
    reference: "Referencia",
    amount: "Monto",
    total: "Total",

    // aria-labels
    editQuantityAriaLabel: "Editar cantidad",
    editUnitPriceAriaLabel: "Editar precio unitario",

    // Status-filter options — status labels now come from enums:status (#182).
    allOption: "Todos",

    // Unit picker (the sale unit, e.g. "3 Dozen") — text equals the enum value.
    // NOTE (flag for native review): "Flat" (a 2.5/30-egg flat) has no single
    // standard Spanish term across regions (cubeta/maple/panel all in use);
    // "Cubeta" chosen as the common Mexican-Spanish term.
    unitEgg: "Huevo",
    unitDozen: "Docena",
    unitFlat: "Cubeta",
    unitTray: "Bandeja",
    unitCarton: "Cartón",
    unitCase: "Caja",

    // Payment-method picker — text equals the enum value.
    methodCash: "Efectivo",
    methodCheck: "Cheque",
    methodCard: "Tarjeta",
    methodBankTransfer: "Transferencia bancaria",
    methodMobilePayment: "Pago móvil",
    methodOther: "Otro",

    // Misc UI text
    addCustomerFirst: "Agregue un cliente primero (Customers page) y luego cree un pedido.",
    noOrdersMatch: "Ningún pedido coincide.",
    voidingNeedsAdmin: "Anular requiere un administrador.",
    voidReasonLabel: "Motivo de anulación: {{reason}}",
    orderTotal: "Total: {{amount}}",
    perUnit: "por {{unit}}",
    eggsCount: "({{count}} huevos)",
    // Interleaves JSX (<strong> around "outstanding …") — rendered via <Trans>.
    paymentsSummary: "Pagado {{paid}} — <strong>pendiente {{outstanding}}</strong>",

    // Inline validation messages
    enterValidAmount: "Ingrese un monto válido.",
    noDecimalPlaces: "Esta moneda no tiene decimales.",
    atMostDecimals: "Como máximo {{count}} decimales para esta moneda.",
    enterAmountGreaterThanZero: "Ingrese un monto mayor que cero.",
    invalidUnitPrice: "Precio unitario inválido.",
    // machine-drafted (#182) — pending native review.
    quantityMustBeWholeNumber: "La cantidad debe ser un número entero.",
    loadSalesDataFailed: "No se pudieron cargar los datos de ventas. ¿Está la API activa?",
    loadOrdersFailed: "No se pudieron cargar los pedidos.",
    loadPaymentsFailed: "No se pudieron cargar los pagos de este pedido.",

    // Confirm / askReason dialogs (title / body / confirmLabel)
    confirmOrderTitle: "¿Confirmar este pedido?",
    confirmOrderBody:
      "Las existencias se asignan del inventario, primero los lotes más antiguos (FIFO). " +
      "Una confirmación por error se puede deshacer con Anular, que devuelve las existencias.",
    confirmOrderConfirmLabel: "Confirmar pedido",
    cancelDraftTitle: "¿Cancelar este borrador?",
    cancelDraftBody: "El pedido pasa a cancelado y ya no se puede editar ni confirmar.",
    voidPaymentTitle: "¿Anular este pago?",
    voidPaymentBody: "El monto pendiente del pedido aumenta de nuevo por el valor del pago.",
    voidPaymentConfirmLabel: "Anular pago",
    voidOrderTitle: "¿Anular este pedido confirmado?",
    voidOrderBody: "Las existencias asignadas vuelven exactamente a los lotes de huevos de donde provinieron.",
    voidOrderConfirmLabel: "Anular pedido",

    // Templated success messages
    orderConfirmed: "Pedido {{ref}} confirmado — existencias asignadas (FIFO).",
    draftOrderCancelled: "Pedido borrador cancelado.",
    paymentRecorded: "Pago registrado.",
    paymentVoided: "Pago anulado — el monto pendiente aumentó de nuevo.",
    orderVoided: "Pedido {{ref}} anulado — existencias devueltas al inventario.",
  },

  // machine-drafted (#182) — pending native review. Task CT2 (B2 catch-up):
  // backfilling es for the Daily entry capture screen (Task 11, en.ts, batch
  // B2). Keys mirror en.ts dailyEntry exactly, including the
  // {{status}}/{{n}}/{{count}}/{{grade}}/{{losses}}/{{total}}/{{cracked}}/
  // {{dirty}}/{{discarded}} placeholders (no <Trans> tags in this namespace).
  // The entry-locked banner's status word goes through the `enums`
  // statusLabel helper, not a key here.
  dailyEntry: {
    title: "Registro diario",

    // Imperative messages
    loadFlocksGradesFailed: "No se pudieron cargar los lotes/grados. ¿Está la API activa?",
    deepLinkUnavailable:
      "Este enlace de edición apunta a un lote o fecha que ya no está "
      + "disponible — se usan los valores predeterminados habituales en su lugar.",

    // "Editing draft" badge
    editingDraftBadge: "Editando borrador",

    // Flock + date context row
    flockLabel: "Lote",
    noFlocksYetOption: "— aún no hay lotes —",
    depletedFlockSuffix: " — agotado, solo para registrar fechas pasadas",
    dateLabel: "Fecha",
    newFlockButton: "+ nuevo lote",

    // New-flock dialog
    newFlockDialogTitle: "Nuevo lote",
    nameLabel: "Nombre",
    breedLabel: "Raza",
    placedLabel: "Colocación",
    birdsLabel: "Aves",
    createFlockButton: "Crear lote",

    // Locked-day / prefill-failure banners
    entryLockedBanner:
      "Este día ya está {{status}} — sus lotes de huevos existen. "
      + "Las correcciones se hacen desde Historial (administradores: ajustar o anular).",
    prefillFailedBanner:
      "No se pudo comprobar si este día ya tiene un registro — el guardado "
      + "está bloqueado para no sobrescribir datos existentes.",

    // Step headings
    stepLabel: "Paso {{n}}",
    stepOfTotal: "de 2:",
    eggCountsHeading: "Conteo de huevos",
    gradingHeading: "Clasificación",

    // Count field labels
    totalEggsLabel: "Total de huevos",
    crackedLabel: "Rotos",
    dirtyLabel: "Sucios",
    discardedLabel: "Descartados",
    mortalityLabel: "Mortalidad",

    // Reconciliation readouts (counts pane)
    countsExceedTotalMessage:
      "Rotos + sucios + descartados ({{losses}}) superan el total de huevos ({{total}}).",
    sellableLabel: "Vendibles",
    sellableFormula: "{{total}} − {{cracked}} − {{dirty}} − {{discarded}}",
    deactivatedGradeSuffix: " (desactivado)",

    // Remainder-assignment gesture (grading pane)
    takeRemainderAriaLabel: "Poner los {{count}} restantes en {{grade}}",
    takeRemainderButton: "+{{count}}",
    armAriaLabel: "Elegir un grado para los {{count}} restantes",
    disarmAriaLabel: "Cancelar la elección de grado",
    armButton: "poner todo en…",
    disarmButton: "elegir un grado…",

    // The `grading` derived object's copy (chip + pinned footer)
    fixCountsFirst: "Corrija los conteos primero",
    fixCountsShort: "corrija los conteos",
    overSellableCount: "por encima del conteo vendible",
    overShort: "de sobra",
    gradedDayAddsUp: "clasificados — el día cuadra",
    allGradedShort: "todo clasificado",
    leftToGrade: "por clasificar",
    leftShort: "restantes",

    // Pinned footer (phone-only summary + saves)
    countsExceedFooterMessage: "Las pérdidas superan el total — corrija los conteos",
    // #446
    daySupportFeed: "Alimento: {{count}} registros (est. {{cost}})",
    daySupportFeed_one: "Alimento: {{count}} registro (est. {{cost}})",
    // Cost dropped when the day's rows span currencies — never a blended sum.
    daySupportFeedNoCost: "Alimento: {{count}} registros",
    daySupportFeedNoCost_one: "Alimento: {{count}} registro",
    daySupportFeedNone: "Alimento: 0 registros",
    daySupportWater: "Agua: {{count}} registros",
    daySupportWater_one: "Agua: {{count}} registro",
    daySupportWaterNone: "Agua: 0 registros",
    stepperUnitCaption: "Contando por {{unit}} — cada toque de − / + mueve {{count}} huevos. Escribir sigue ingresando números exactos.",
    sellableWord: "vendible",
    saveDraftButton: "Guardar borrador",
    submitButton: "Guardar y enviar (crea lotes de huevos)",

    // Submit confirmation dialog (one-way action, #59)
    confirmSubmitTitle: "¿Enviar este día?",
    confirmSubmitBody:
      "Se crean los lotes de huevos y el registro ya no se puede editar. "
      + "Las correcciones después de esto necesitan un ajuste de un gerente.",
    confirmSubmitLabel: "Enviar día",

    // Save-result messages
    submittedMessage: "Enviado — se crearon {{count}} lote(s) de huevos.",
    draftSavedMessage: "Borrador guardado.",
  },

  // machine-drafted (#182) — pending native review. Task CT2 (B2 catch-up):
  // backfilling es for the Dashboard landing screen (Task 12, en.ts, batch
  // B2). Keys mirror en.ts dashboard exactly, including the {{count}}
  // placeholder (no <Trans> tags in this namespace). The two status pills on
  // this screen go through the `enums` statusLabel helper, not a key here.
  dashboard: {
    title: "Panel",

    // Imperative messages
    loadFailed: "No se pudo cargar el panel. ¿Está la API activa?",
    panelLoadError: "No se pudo cargar.",

    // Stat row
    statEggsCollectedToday: "Huevos recolectados hoy",
    statEggsAvailable: "Huevos disponibles",
    statActiveFlocks: "Lotes activos",

    // "Today" panel (per-flock production)
    todayPanelTitle: "Hoy",
    noFlocksMessage: "Aún no hay lotes — cree uno en la página de Registro diario.",
    flockHeader: "Lote",
    statusHeader: "Estado",
    eggsHeader: "Huevos",
    lossesHeader: "Pérdidas",
    mortalityHeader: "Mortalidad",
    noEntryBadge: "sin registro",

    // "Stock" panel (by grade)
    stockPanelTitle: "Existencias",
    noStockMessage: "Aún no hay existencias — registre y envíe un registro diario.",
    gradeHeader: "Grado",
    availableHeader: "Disponible",
    restrictedHeader: "Restringido",
    eggsAvailableMessage: "{{count}} huevos disponibles.",

    // "Recent sales" panel (hidden for ReadOnly/Denied, #127)
    salesPanelTitle: "Ventas recientes",
    noOrdersMessage: "Aún no hay pedidos.",
    refHeader: "Ref.",
    customerHeader: "Cliente",
    totalHeader: "Total",
  },

  // machine-drafted (#182) — pending native review. Task CT2 (B2 catch-up):
  // backfilling es for the Water capture + correction screen (Task 13, en.ts,
  // batch B2). Keys mirror en.ts water exactly, including the {{unit}}
  // placeholder (no <Trans> tags in this namespace). Source/Unit picker
  // values go through the `enums` waterSourceLabel/waterUnitLabel helpers,
  // not a key here.
  // #446 — machine-drafted, pending native review (translate-now policy).
  feed: {
    title: "Alimento",
    loadFailed: "No se pudieron cargar las parvadas y los artículos de alimento. ¿Está activa la API?",
    loadRecordsFailed: "No se pudieron cargar los registros de alimento.",
    loadMoreFailed: "No se pudo cargar más.",
    intro:
      "Registre lo que se alimentó a cada parvada. El inventario se descuenta "
      + "de las compras más antiguas primero y el costo estimado proviene de esos lotes.",
    flockLabel: "Parvada",
    depletedFlockSuffix: " — agotada, solo registro retroactivo",
    itemLabel: "Artículo",
    itemOption: "{{name}} ({{onHand}} {{unit}} disponibles)",
    dateLabel: "Fecha",
    quantityLabel: "Cantidad",
    quantityLabelWithUnit: "Cantidad ({{unit}})",
    noteLabel: "Nota",
    recordFeedButton: "Registrar alimento",
    quantityMustBePositive: "La cantidad debe ser un número positivo.",
    recordedMessage: "Alimento registrado.",
    correctionsHint:
      "Una alimentación mal ingresada se corrige con un ajuste de Inventario en el "
      + "lote afectado — los registros de alimento nunca se editan.",
    filterFlockLabel: "Filtrar por parvada",
    inactiveItemSuffix: " — inactivo, consumiendo existencias restantes",
    inactiveEmptyItemSuffix: " — inactivo, sin existencias",
    recordsHeading: "Registros",
    fromLabel: "Desde",
    toLabel: "Hasta",
    noRecordsMatch: "Ningún registro de alimento coincide.",
    dateHeader: "Fecha",
    flockHeader: "Parvada",
    itemHeader: "Artículo",
    amountHeader: "Cantidad",
    estimatedCostHeader: "Costo est.",
    noteHeader: "Nota",
    loadMoreButton: "cargar más",
  },
  water: {
    title: "Agua",

    // Imperative messages
    loadFlocksFailed: "No se pudieron cargar los lotes. ¿Está la API activa?",
    loadRecordsFailed: "No se pudieron cargar los registros de agua.",
    loadMoreFailed: "No se pudo cargar más.",
    concurrentEditError:
      "Este registro se acaba de cambiar en otro lugar — recargue la lista "
      + "e inténtelo de nuevo.",

    intro:
      "Registre lo que bebió cada lote — una cantidad directa, o lecturas de "
      + "medidor (la cantidad es la diferencia del medidor). Los registros se "
      + "pueden corregir después; el lote y la fecha son fijos.",

    // Capture form labels
    flockLabel: "Lote",
    depletedFlockSuffix: " — agotado, solo para registrar fechas pasadas",
    dateLabel: "Fecha",
    sourceLabel: "Fuente",
    unitLabel: "Unidad",
    fromMeterReadingsLabel: "a partir de lecturas de medidor",
    meterStartLabel: "Medidor inicial",
    meterEndLabel: "Medidor final",
    quantityLabelWithUnit: "Cantidad ({{unit}})",
    noteLabel: "Nota",

    // Capture form buttons
    recordWaterButton: "Registrar agua",
    saveCorrectionButton: "Guardar corrección",
    cancelEditButton: "cancelar edición",

    // Inline validation messages
    quantityMustBePositive: "La cantidad debe ser un número positivo.",
    bothMeterReadingsRequired: "Se requieren ambas lecturas del medidor.",

    // Save-result messages
    recordedMessage: "Agua registrada.",
    recordCorrectedMessage: "Registro de agua corregido.",

    // Records list — filters
    recordsHeading: "Registros",
    fromLabel: "Desde",
    toLabel: "Hasta",
    noRecordsMatch: "Ningún registro de agua coincide.",

    // Records table
    dateHeader: "Fecha",
    flockHeader: "Lote",
    amountHeader: "Cantidad",
    sourceHeader: "Fuente",
    metersHeader: "Medidores",
    noteHeader: "Nota",
    correctButton: "corregir",
    loadMoreButton: "cargar más",
  },

  // machine-drafted (#182) — pending native review. Task CT2 (B2 catch-up):
  // backfilling es for the Egg grade catalog admin screen (Task 14, en.ts,
  // batch B2 — the last B2 screen). Keys mirror en.ts grades exactly (no
  // placeholders in this namespace). The Type picker/cell goes through the
  // `enums` gradeTypeLabel helper, and the Active/Inactive status pill
  // through `enums` statusLabel — neither is a key here. Grade NAMES (g.name)
  // are free-form farm data and stay raw, never routed through the catalog.
  grades: {
    title: "Grados de huevo",
    loadingTitle: "Grados",

    // Imperative message
    loadGradesFailed: "No se pudieron cargar los grados. ¿Está la API activa?",

    intro:
      "Los grados vendibles aparecen en los selectores de registro diario y "
      + "de pedidos. Desactivar un grado lo elimina de los selectores; las "
      + "existencias y el historial existentes no se ven afectados.",

    // Buttons
    newGradeButton: "Nuevo grado",
    newGradeDialogTitle: "Nuevo grado",
    editGradeDialogTitle: "Editar grado",
    addGradeButton: "Agregar grado",
    editButton: "editar",
    deactivateButton: "desactivar",
    activateButton: "activar",

    // Create-dialog form labels
    nameLabel: "Nombre *",
    typeLabel: "Tipo",
    sortLabel: "Orden",
    saleableLabel: "vendible",
    editNameLabel: "Nombre",

    // Table headers
    nameHeader: "Nombre",
    typeHeader: "Tipo",
    sortHeader: "Orden",
    saleableHeader: "Vendible",
    statusHeader: "Estado",

    // Saleable column's "yes" badge
    saleableYesBadge: "sí",
  },

  // machine-drafted (#182) — pending native review. Task CT3 (B3 catch-up):
  // backfilling es for the Feed & inventory screen (Task 16, en.ts, batch
  // B3 — first of four B3 screens). Keys mirror en.ts inventory exactly,
  // including the {{name}}/{{quantity}}/{{unit}}/{{category}}/{{code}}
  // placeholders (no <Trans> tags in this namespace). Category and
  // movement-type displays go through the `enums`
  // inventoryCategoryLabel/inventoryMovementLabel helpers, not a key here —
  // the {{category}} placeholder in notFeedableMessage IS that
  // already-labelled value, per en.ts's own comment. "Lote" used throughout
  // for "lot" (a received inventory batch), matching the poultry-farm sense
  // of "lote" already established for nav.flocks/dailyEntry's "lote(s) de
  // huevos".
  inventory: {
    title: "Alimento e inventario",
    intro:
      "Reciba existencias como compras; cada cambio queda en el registro de "
      + "movimientos del artículo. El registro de uso de alimento contra los "
      + "lotes llega después.",

    // Imperative messages
    loadInventoryFailed: "No se pudo cargar el inventario. ¿Está la API activa?",
    invalidCostError: "Costo inválido.",
    itemCreatedMessage: "Artículo creado.",
    loadLedgerFailed: "No se pudo cargar el registro de movimientos.",
    quantityMustBePositive: "La cantidad debe ser un número positivo.",
    purchaseRecordedMessage: "Compra registrada — existencias recibidas.",
    adjustQuantityRequired:
      "La cantidad de ajuste debe ser un número distinto de cero (negativo "
      + "quita existencias).",
    adjustReasonRequired: "Se requiere un motivo para las correcciones.",
    correctionRecordedMessage: "Corrección registrada en el registro de movimientos.",

    // Page-head button + New/edit item dialogs
    newItemButton: "Nuevo artículo",
    newItemDialogTitle: "Nuevo artículo de inventario",
    editItemDialogTitle: "Editar artículo",
    itemNameLabel: "Nombre del artículo *",
    editItemNameLabel: "Nombre del artículo",
    categoryLabel: "Categoría",
    unitLabel: "Unidad *",
    editUnitLabel: "Unidad",
    defaultCostLabel: "Costo predeterminado/unidad",
    addItemButton: "Agregar artículo",

    // Item panel (opened item)
    itemPanelHeading: "{{name}} — {{quantity}} {{unit}} disponible(s)",
    recordPurchaseButton: "Registrar compra",
    recordUsageLink: "Registrar uso en la página de Alimento",
    correctStockButton: "Corregir existencias",
    notFeedableMessage:
      "Los artículos de {{category}} no se dan de comer a los lotes — el "
      + "uso solo aplica a artículos de Alimento, Suplemento y Aditivo.",
    correctionsNeedAdminMessage: "Las correcciones de existencias requieren un administrador.",
    noLotsMessage: "Aún no hay lotes — las correcciones se aplican a un lote recibido.",

    // Record-purchase dialog
    recordPurchaseDialogTitle: "Registrar compra — {{name}}",
    receivedLabel: "Recibido",
    quantityLabelWithUnit: "Cantidad ({{unit}})",
    unitCostLabel: "Costo unitario",
    unitCostWithCurrencyLabel: "Costo unitario ({{code}})",
    costPlaceholderItemDefault: "predeterminado del artículo",
    costPlaceholderRequired: "requerido",
    lotNumberLabel: "N.º de lote",
    expiryLabel: "Vencimiento",
    noteLabel: "Nota",
    recordPurchaseSubmitButton: "Registrar compra",

    // Record-usage dialog
    flockLabel: "Lote",
    depletedFlockSuffix: " (agotado — solo para registrar fechas pasadas)",
    dateLabel: "Fecha",

    // Correct-stock dialog
    correctStockDialogTitle: "Corregir existencias — {{name}}",
    lotFieldLabel: "Lote",
    typeLabel: "Tipo",
    adjustTypeAdjustmentOption: "Ajuste (±)",
    adjustTypeDiscardOption: "Desecho (baja)",
    adjustQuantityPlaceholderDiscard: "cantidad desechada",
    adjustQuantityPlaceholderCorrection: "± corrección",
    reasonLabel: "Motivo *",
    recordCorrectionButton: "Registrar corrección",

    // Movement ledger table
    ledgerDateHeader: "Fecha",
    ledgerTypeHeader: "Tipo",
    ledgerQuantityHeader: "Cantidad",
    ledgerNoteHeader: "Nota",
    noMovementsMessage: "Aún no hay movimientos — registre una compra arriba.",
    closeButton: "cerrar",

    // Items table
    nameHeader: "Nombre",
    categoryHeader: "Categoría",
    onHandHeader: "Disponible",
    defaultCostHeader: "Costo predeterminado",
    statusHeader: "Estado",
    openButton: "abrir",
    editButton: "editar",
    deactivateButton: "desactivar",
    activateButton: "activar",
  },

  // machine-drafted (#182) — pending native review. Task CT3 (B3 catch-up):
  // backfilling es for the Product catalog + packed-unit conversions screen
  // (Task 17, en.ts, batch B3 — second B3 screen). Keys mirror en.ts
  // products exactly, including the {{count}}/{{code}}/{{unitCode}}
  // placeholders (no <Trans> tags in this namespace). Active/Inactive status
  // on both tables goes through the `enums` statusLabel helper, not a key
  // here. Product/grade names and unitCode are free-form farm data and stay
  // raw — never routed through the catalog.
  products: {
    title: "Productos",

    // Imperative messages
    loadCatalogFailed: "No se pudo cargar el catálogo. ¿Está la API activa?",
    enterPriceAsNumber: "Ingrese el precio como un número simple.",
    noDecimalPlaces: "Esta moneda no tiene decimales.",
    atMostDecimals: "Como máximo {{count}} decimales para esta moneda.",

    intro:
      "Lo que vende la granja. Cada producto de huevo se asocia a un grado "
      + "de huevo — las ventas toman existencias de los lotes de ese grado. "
      + "Desactivar quita un producto de los selectores; el historial "
      + "conserva su nombre.",

    // Page-head button + New/edit product dialogs
    newProductButton: "Nuevo producto",
    newProductDialogTitle: "Nuevo producto",
    editProductDialogTitle: "Editar producto",

    // Product form labels
    nameLabel: "Nombre",
    gradeLabel: "Grado",
    pickGradeOption: "Elegir un grado…",
    soldPerLabel: "Se vende por",
    defaultPriceLabel: "Precio predeterminado",
    defaultPriceWithCurrencyLabel: "Precio predeterminado ({{code}})",
    priceOptionalPlaceholder: "opcional",
    notesLabel: "Notas",
    addProductButton: "Agregar producto",

    // Packed-unit (egg-unit-conversion) dialog
    eggsPerUnit: "Huevos por {{unitCode}}",
    packedUnitDialogTitle: "Unidad de empaque",
    eggsPerUnitFieldLabel: "Huevos por unidad",
    activeCheckboxLabel: "activo",

    // Products table
    noProductsMessage: "Aún no hay productos.",
    nameHeader: "Nombre",
    gradeHeader: "Grado",
    soldPerHeader: "Se vende por",
    defaultPriceHeader: "Precio predeterminado",
    statusHeader: "Estado",
    editButton: "editar",
    deactivateButton: "desactivar",
    activateButton: "activar",

    // Packed units table
    packedUnitsHeading: "Unidades de empaque",
    packedUnitsIntro:
      "Cuántos huevos contiene cada unidad al vender (un cartón es de 12, "
      + "18 o 30 según su mercado — configure el suyo). Cambiar una unidad "
      + "solo afecta las ventas futuras; los pedidos ya registrados "
      + "conservan la cantidad con la que se vendieron.",
    unitHeader: "Unidad",
    eggsPerUnitHeader: "Huevos por unidad",
    alwaysOneMessage: "siempre 1",
  },

  // machine-drafted (#182) — pending native review. Task CT3 (B3 catch-up):
  // backfilling es for the Egg stock summary + drill-down screen (Task 18,
  // en.ts, batch B3 — third B3 screen). Keys mirror en.ts stock exactly,
  // including the {{available}}/{{grades}} placeholders (no <Trans> tags in
  // this namespace). The movement ledger's Type cell goes through the
  // `enums` stockMovementLabel helper, not a key here. Grade/lot names and
  // quantity values are free-form farm data and stay raw.
  stock: {
    title: "Existencias",

    // Imperative messages
    loadStockFailed: "No se pudieron cargar las existencias. ¿Está la API activa?",
    loadLotsFailed: "No se pudieron cargar los lotes del grado.",
    loadMovementsFailed: "No se pudieron cargar los movimientos del lote.",

    noStockMessage: "Aún no hay existencias — registre y envíe un registro diario.",

    // By-grade stock table
    gradeHeader: "Grado",
    availableHeader: "Disponible",
    restrictedHeader: "Restringido",
    lotsButton: "lotes",
    hideLotsButton: "ocultar lotes",
    totalAvailableMessage:
      "{{available}} huevos disponibles en {{grades}} grado(s). Restringido "
      + "= en retiro de medicamentos, bloqueado para la venta.",

    // Lots drill-down (per grade)
    lotsHeading: "Lotes",
    noLotsMessage: "Aún no hay lotes para este grado.",
    producedOnHeader: "Fecha de producción",
    producedHeader: "Producido",
    historyButton: "historial",
    hideHistoryButton: "ocultar historial",

    // Movement ledger drill-down (per lot)
    movementLedgerHeading: "Registro de movimientos",
    movementLedgerIntro:
      "Cada cambio en los huevos disponibles de este lote — la suma "
      + "acumulada siempre es igual al saldo de arriba.",
    ledgerWhenHeader: "Cuándo (UTC)",
    ledgerTypeHeader: "Tipo",
    ledgerChangeHeader: "Cambio",
    ledgerReasonHeader: "Motivo",
  },

  // machine-drafted (#182) — pending native review. Task CT3 (B3 catch-up):
  // backfilling es for the Flock roster + bird ledger screen (Task 19,
  // en.ts, batch B3 — last B3 screen). Keys mirror en.ts flocks exactly,
  // including the {{name}}/{{count}}/{{weeks}} placeholders (no <Trans>
  // tags in this namespace). The bird-ledger Type picker/cell and the
  // flocks table's Status badge go through the `enums`
  // flockMovementLabel/statusLabel helpers, not a key here. Flock
  // name/breed are free-form farm data and stay raw.
  flocks: {
    title: "Lotes",

    // Imperative messages
    loadFlocksFailed: "No se pudieron cargar los lotes. ¿Está la API activa?",
    loadMovementsFailed: "No se pudieron cargar los movimientos.",

    newFlockButton: "Nuevo lote",
    intro:
      "Agote cuando ya no queden aves; archive para ocultar un lote de los "
      + "selectores y del panel. El historial sigue resolviendo los nombres "
      + "de los lotes archivados.",

    // New-flock dialog
    newFlockDialogTitle: "Nuevo lote",
    nameLabel: "Nombre *",
    breedLabel: "Raza *",
    placedLabel: "Colocación",
    birdsLabel: "Aves",
    addFlockButton: "Agregar lote",

    // Edit-flock dialog
    editFlockDialogTitle: "Editar lote",
    editNameLabel: "Editar nombre",
    editBreedLabel: "Editar raza",
    editPlacedLabel: "Editar fecha de colocación",
    editCountLabel: "Editar cantidad de aves",

    // Show-archived toggle
    showArchivedLabel: "mostrar {{count}} archivado(s)",

    noFlocksMessage: "Aún no hay lotes.",

    // Flocks table
    nameHeader: "Nombre",
    breedHeader: "Raza",
    placedHeader: "Colocación",
    ageHeader: "Edad",
    birdsHeader: "Aves",
    statusHeader: "Estado",
    ageWeeksSuffix: "{{weeks}} sem",

    // Row actions
    editButton: "editar",
    depleteButton: "agotar",
    archiveButton: "archivar",
    reactivateButton: "reactivar",
    openLedgerButton: "aves",
    closeLedgerButton: "cerrar",

    // Deplete/archive confirm dialogs
    depleteConfirmTitle: "¿Agotar \"{{name}}\"?",
    depleteConfirmBody:
      "El lote deja de aceptar nuevos registros. El registro de fechas "
      + "pasadas (backfill) sigue funcionando.",
    depleteConfirmLabel: "Agotar lote",
    archiveConfirmTitle: "¿Archivar \"{{name}}\"?",
    archiveConfirmBody:
      "Desaparece de los selectores y del panel, y no acepta nada nuevo.",
    archiveConfirmLabel: "Archivar lote",

    // Bird ledger panel
    ledgerHeading: "Registro de aves — {{name}}",
    ledgerIntro: "Las filas de mortalidad provienen de los registros diarios enviados.",
    ledgerIntroAdminNote:
      " Registre los descartes aquí; use un ajuste negativo para corregir un "
      + "conteo incorrecto.",
    ledgerIntroWorkerNote: " Registrar descartes y ajustes requiere un administrador.",
    recordMovementButton: "Registrar movimiento",

    // Record-movement dialog
    recordMovementDialogTitle: "Registrar movimiento de aves",
    dateLabel: "Fecha",
    typeLabel: "Tipo",
    noteLabel: "Nota",
    recordButton: "Registrar",

    noMovementsMessage: "Aún no hay movimientos — el lote está en su conteo inicial.",

    // Movement ledger table
    ledgerDateHeader: "Fecha",
    ledgerTypeHeader: "Tipo",
    ledgerBirdsHeader: "Aves",
    ledgerNoteHeader: "Nota",
  },

  // machine-drafted (#182) — pending native review. Task 25c (B4): new
  // namespace, backfilling es so Spanish mode renders translated text on the
  // Settings screen. Keys mirror en.ts settings exactly, including the
  // {{cap}}/{{actualKb}}/{{limitKb}}/{{code}} placeholders and the <strong>
  // tag in logoSquareHint.
  settings: {
    heading: "Configuración de la granja",
    intro:
      "Cómo se llama esta granja, y la configuración regional, la zona "
      + "horaria y la moneda en que registra y lee su trabajo.",
    loadFailedMessage: "No se pudo cargar la configuración de la granja.",

    // Logo panel
    logoSectionHeading: "Logotipo",
    logoAlt: "Logotipo actual de la granja",
    logoLoadingMessage: "Cargando el logotipo…",
    logoLoadFailedMessage: "No se pudo cargar el logotipo.",
    logoNoneMessage: "No hay logotipo configurado — la barra lateral muestra la marca de Cluckwork.",
    uploadLogoButton: "Subir un logotipo",
    replaceLogoButton: "Reemplazar el logotipo",
    removeLogoButton: "Quitar",
    logoRulesHint:
      "PNG, JPEG o WebP, de hasta {{cap}} y 4096 px por lado. No se aceptan "
      + "imágenes animadas. La imagen se guarda reescrita, sin los metadatos "
      + "de cámara ni ubicación.",
    logoSquareHint:
      "Use una imagen <strong>cuadrada</strong> — el logotipo se muestra "
      + "pequeño en la barra lateral, así que una marca simple y bien "
      + "recortada (un símbolo o una sola letra) se ve mucho mejor ahí que "
      + "una imagen ancha o detallada. Un fondo transparente sobre un diseño "
      + "claro funciona mejor.",
    logoWorkingMessage: "Procesando…",
    logoUpdatedMessage: "Logotipo actualizado.",
    logoRemovedMessage: "Logotipo eliminado.",
    logoOversizeMessage: "Esa imagen pesa {{actualKb}} KB. El límite es {{limitKb}} KB.",
    removeLogoConfirmTitle: "¿Quitar el logotipo de la granja?",
    removeLogoConfirmBody:
      "La barra lateral vuelve a mostrar la marca de Cluckwork. Puede subir "
      + "otro en cualquier momento.",
    removeLogoConfirmLabel: "Quitar logotipo",

    // Localization form
    localizationSectionHeading: "Localización",
    farmNameLabel: "Nombre de la granja",
    timezoneLabel: "Zona horaria",
    timezoneUnknownWarning:
      "Este navegador no reconoce esa zona horaria, así que las fechas aquí "
      + "seguirían al dispositivo en lugar de a la granja. Elija una de la lista.",
    localeLabel: "Configuración regional",
    currencyLabel: "Moneda",
    currencyLockedNote:
      "La moneda está fija en {{code}}: esta granja ya registró montos en "
      + "ella. El dinero registrado nunca se vuelve a valorar, así que "
      + "cambiar esto dejaría cada total guardado con otro significado.",
    unitSystemLabel: "Sistema de unidades",
    defaultStepperUnitLabel: "Unidad de conteo de la entrada diaria",
    defaultStepperUnitHint:
      "Cuánto cuentan los botones +/− de la entrada diaria para todos en esta granja — "
      + "por ejemplo Tray para contar por bandeja (30 huevos) en lugar de huevo por huevo. "
      + "Cada persona puede elegir la suya en su pantalla de Cuenta.",
    firstDayOfWeekLabel: "Primer día de la semana",
    followLocaleOption: "Seguir la configuración regional",
    paletteLegend: "Paleta de la granja",
    paletteHint:
      "El color de acento para todos en esta granja. Cada persona sigue "
      + "eligiendo el modo claro o nocturno por sí misma.",
    paletteAubergine: "Berenjena",
    paletteForest: "Bosque",
    paletteSlate: "Pizarra",
    paletteTerracotta: "Terracota",
    dateFormatLabel: "Formato de fecha",
    timeFormatLabel: "Formato de hora",
    customFormatOption: "Personalizado…",
    customDateFormatLabel: "Formato de fecha personalizado",
    customTimeFormatLabel: "Formato de hora personalizado",
    savingButton: "Guardando…",
    saveButton: "Guardar configuración",
    effectNote:
      "La zona horaria se aplica en todas partes en cuanto se guarda. La "
      + "configuración regional, el sistema de unidades y los formatos "
      + "personalizados se registran contra la granja y determinarán cómo "
      + "se muestran los montos, las fechas y las medidas una vez que ese "
      + "formato esté disponible.",
    savedMessage: "Configuración guardada.",

    // Imperative messages
    versionConflictMessage:
      "Otra persona cambió esta configuración mientras esta pantalla "
      + "estaba abierta. Vuelva a cargar la página e inténtelo de nuevo.",
    saveReadBackFailedMessage:
      "Guardado. Esta pantalla no pudo volver a leer la configuración — "
      + "vuelva a cargar la página antes de guardar de nuevo.",
    refreshFailedMessage:
      "Guardado. El resto de la aplicación no pudo detectar el cambio — "
      + "vuelva a cargar la página para asegurarse de que se aplicó en "
      + "todas partes.",
  },

  // machine-drafted (#182) — pending native review. Task 25c (B4): new
  // namespace, backfilling es so Spanish mode renders translated text on the
  // Users screen. Keys mirror en.ts users exactly, including the
  // {{label}}/{{email}}/{{role}} placeholders.
  users: {
    heading: "Usuarios",
    newUserButton: "Nuevo usuario",
    roleDescription:
      "Los trabajadores registran el trabajo del día (opcionalmente "
      + "limitado a los lotes asignados). Los gerentes además corrigen, "
      + "anulan y configuran. Ventas gestiona clientes, pedidos y pagos. "
      + "Solo lectura ve existencias, historial e informes. Administrador "
      + "(propietario) hace todo, incluida la gestión de usuarios.",

    // Create-user dialog
    emailFieldLabel: "Correo electrónico *",
    passwordFieldLabel: "Contraseña (mín. 12 caracteres) *",
    nameFieldLabel: "Nombre",
    roleFieldLabel: "Rol",
    adminRoleOption: "{{label}} (propietario)",
    createUserButton: "Crear usuario",

    // #308 — re-confirmación de contraseña (machine-drafted, pending native review)
    stepUpFieldLabel: "Tu contraseña actual *",
    stepUpCreateHint: "Crear otro propietario requiere volver a ingresar tu contraseña actual.",
    stepUpResetHint: "Restablecer la contraseña de un propietario requiere volver a ingresar tu contraseña actual.",

    // Users table
    emailColumnHeader: "Correo electrónico",
    nameColumnHeader: "Nombre",
    roleColumnHeader: "Rol",
    editButton: "editar",
    resetPasswordButton: "contraseña",
    flocksButton: "lotes",

    // Flock-access dialog
    flockAccessTitle: "Acceso a lotes — {{email}}",
    flockAccessHint:
      "Sin asignaciones = el trabajador puede registrar para cualquier "
      + "lote. La primera asignación lo limita solo a los lotes indicados.",
    noAssignmentsMessage: "Sin asignaciones — acceso a toda la cuenta.",
    removeAssignmentButton: "quitar",
    assignFlockButton: "Asignar lote",
    doneButton: "Listo",

    // Edit-user dialog
    editUserTitle: "Editar usuario — {{email}}",
    clearNameHint: "Deje en blanco para borrar el nombre.",

    // Set-password dialog
    setPasswordTitle: "Establecer contraseña — {{email}}",
    passwordDialogHint:
      "No necesita la contraseña actual. Al establecerla se cierra la "
      + "sesión en todos los dispositivos — comunique la nueva contraseña "
      + "directamente.",
    newPasswordFieldLabel: "Nueva contraseña (mín. 12 caracteres) *",
    confirmPasswordFieldLabel: "Confirmar nueva contraseña *",
    setPasswordButton: "Establecer contraseña",

    // Imperative messages
    createSuccessMessage: "Cuenta de {{role}} creada para {{email}}.",
    passwordMismatchMessage: "Las contraseñas no coinciden.",
    passwordSetMessage: "Contraseña establecida para {{email}}. Se cerró la sesión en todos los dispositivos.",
    updatedMessage: "Se actualizó {{email}}.",
  },

  // machine-drafted (#182) — pending native review. Task 25c (B4): new
  // namespace, backfilling es so Spanish mode renders translated text on the
  // Expenses screen. Keys mirror en.ts expenses exactly, including the
  // {{name}}/{{count}}/{{amount}}/{{code}}/{{date}}/{{description}}
  // placeholders.
  expenses: {
    title: "Gastos",

    // Imperative messages
    expenseRecordedMessage: "Gasto registrado.",
    expenseCorrectedMessage: "Gasto corregido.",
    conflictRebindMessage:
      "Este gasto fue cambiado por otra persona — el formulario ahora "
      + "muestra los valores más recientes; vuelva a aplicar su corrección.",
    categoryCreatedMessage: "Categoría creada.",
    categoryDeactivatedMessage: "Categoría \"{{name}}\" desactivada.",
    categoryReactivatedMessage: "Categoría \"{{name}}\" reactivada.",

    // Amount-parsing validation
    enterValidAmount: "Ingrese un monto válido.",
    noDecimalPlaces: "Esta moneda no tiene decimales.",
    atMostDecimals: "Como máximo {{count}} decimales para esta moneda.",
    enterAmountGreaterThanZero: "Ingrese un monto mayor que cero.",

    // Filters
    monthLabel: "Mes",
    categoryLabel: "Categoría",
    allCategoriesOption: "Todas las categorías",
    hideCategoriesButton: "ocultar categorías",
    manageCategoriesButton: "gestionar categorías",
    monthTotalLabel: "Total del mes: {{amount}}",

    // Category-management panel
    categoriesHeading: "Categorías de gastos",
    newCategoryButton: "Nueva categoría",
    newCategoryDialogTitle: "Nueva categoría de gasto",
    categoryNameLabel: "Nombre de la categoría",
    addCategoryButton: "Agregar categoría",
    deactivatedSuffix: " (desactivada)",
    deactivateButton: "desactivar",
    reactivateButton: "reactivar",
    noCategoriesMessage: "Aún no hay categorías — agregue una arriba.",

    // Record-expense form
    recordExpenseHeading: "Registrar un gasto",
    dateLabel: "Fecha",
    pickOption: "— elegir —",
    descriptionLabel: "Descripción",
    amountLabel: "Monto ({{code}})",
    flockOptionalLabel: "Lote (opcional)",
    noneOption: "— ninguno —",
    noteOptionalLabel: "Nota (opcional)",
    recordExpenseButton: "Registrar gasto",
    addCategoryFirstMessage: "Agregue una categoría primero — todo gasto necesita una.",

    // Correct-expense dialog
    correctExpenseDialogTitle: "Corregir gasto",
    correctExpenseDialogTitleWithExpense: "Corregir — {{date}}, {{description}}",
    saveCorrectionButton: "Guardar corrección",

    // Expenses table
    noExpensesMessage: "No hay gastos este mes.",
    dateHeader: "Fecha",
    categoryHeader: "Categoría",
    descriptionHeader: "Descripción",
    amountHeader: "Monto",
    flockHeader: "Lote",
    noteHeader: "Nota",
    correctButton: "corregir",
    loadMoreButton: "cargar más",
  },

  // machine-drafted (#182) — pending native review. Task 25c (B4): new
  // namespace, backfilling es so Spanish mode renders translated text on the
  // Customers screen. Keys mirror en.ts customers exactly (no placeholders
  // in this namespace).
  customers: {
    title: "Clientes",
    newCustomerButton: "Nuevo cliente",

    // Create-customer dialog
    nameFieldLabel: "Nombre *",
    phoneFieldLabel: "Teléfono *",
    emailFieldLabel: "Correo electrónico",
    addressFieldLabel: "Dirección",
    noteFieldLabel: "Nota",
    addCustomerButton: "Agregar cliente",

    // Imperative messages
    loadCustomersErrorMessage: "No se pudieron cargar los clientes.",
    loadBalancesErrorMessage: "No se pudieron cargar los saldos de los clientes.",

    // Customers table
    noCustomersMessage: "Aún no hay clientes.",
    nameHeader: "Nombre",
    phoneHeader: "Teléfono",
    emailHeader: "Correo electrónico",
    addressHeader: "Dirección",
    noteHeader: "Nota",
    outstandingHeader: "Pendiente",
  },

  // machine-drafted (#182) — pending native review. Task 28 (B5): new
  // namespace, backfilling es so Spanish mode renders translated text on the
  // History screen. Keys mirror en.ts history exactly, including the
  // {{status}}/{{date}}/{{flock}}/{{total}}/{{mortality}}/{{reason}}/{{time}}
  // placeholders. "Daily entry page" (noEntriesMatch) is kept in English,
  // matching the existing sales.addCustomerFirst precedent of leaving an
  // unexternalized screen's name untranslated until dailyEntry itself is
  // added to TRANSLATED_NAMESPACES.
  history: {
    loadingTitle: "Historial",
    title: "Historial de entradas diarias",

    intro:
      "Las entradas presentadas y bloqueadas se pueden ajustar o anular "
      + "aquí — las existencias y el registro de aves se actualizan "
      + "automáticamente; los huevos ya vendidos nunca se modifican. "
      + "Siempre se requiere un motivo.",

    concurrentConflictMessage:
      "Esta entrada acaba de cambiar en otro lugar — la lista se ha vuelto a cargar; vuelva a intentarlo.",
    loadFlocksGradesFailed: "No se pudieron cargar los lotes/grados.",
    loadEntriesFailed: "No se pudieron cargar las entradas.",
    conflictRebindMessage:
      "Esta entrada fue cambiada por otra persona — el formulario muestra "
      + "los valores más recientes; vuelva a aplicar su corrección.",
    nothingToAdjustMessage: "Esta entrada ahora está {{status}} — no queda nada por ajustar.",
    conflictReloadFailedMessage:
      "Esta entrada fue cambiada por otra persona y la lista no se pudo "
      + "volver a cargar — vuelva a cargar la página antes de reintentarlo.",
    gradesMustReconcileMessage:
      "Las cantidades clasificadas deben ser iguales al total de huevos menos los agrietados, sucios y "
      + "descartados.",
    entryAdjustedMessage: "Entrada ajustada — las existencias y el registro de aves se actualizaron para coincidir.",
    adjustReloadFailedMessage: "El ajuste se guardó, pero la lista no se pudo volver a cargar — actualice la página.",
    voidConfirmTitle: "¿Anular la entrada del {{date}} para {{flock}}?",
    voidConfirmBody:
      "Sus lotes de huevos quedan vacíos y sus muertes se revierten. La "
      + "entrada se conserva como Anulada. Se rechaza si alguno de sus "
      + "huevos ya fue vendido.",
    voidConfirmLabel: "Anular entrada",
    entryVoidedMessage: "Entrada anulada — sus lotes de huevos quedaron vacíos y sus muertes se revirtieron.",
    voidReloadFailedMessage: "La anulación se guardó, pero la lista no se pudo volver a cargar — actualice la página.",
    voidConflictMessage:
      "Esta entrada fue cambiada por otra persona — la lista se ha vuelto a cargar; vuelva a intentarlo.",
    voidConflictReloadFailedMessage:
      "Esta entrada fue cambiada por otra persona y la lista no se pudo "
      + "volver a cargar — vuelva a cargar la página.",
    loadMoreFailedMessage: "No se pudo cargar más.",

    // Filters
    flockLabel: "Lote",
    allFlocksOption: "Todos los lotes",
    fromLabel: "Desde",
    toLabel: "Hasta",

    // Adjust dialog
    adjustDialogTitle: "Ajustar entrada",
    adjustDialogTitleWithEntry: "Ajustar — {{date}}, {{flock}}",
    previouslyAdjusted:
      "Ajustado anteriormente (total {{total}}, mortalidad {{mortality}} — \"{{reason}}\").",
    // Los dos pasos, las etiquetas de conteo y el chip de conciliación vienen
    // del espacio `dailyEntry` (mismo formulario).
    inactiveGradeSuffix: " (inactivo)",
    reasonLabel: "Motivo *",
    saveAdjustmentButton: "Guardar ajuste",

    noEntriesMatch: "Ninguna entrada coincide — registre una en la página Daily entry.",

    // Entries table
    dateHeader: "Fecha",
    flockHeader: "Lote",
    statusHeader: "Estado",
    totalHeader: "Total",
    // NOTE (flag for native review): "cr/di/ds" abbreviates the English
    // cracked/dirty/discarded; re-abbreviated here to the initials of the
    // Spanish words (ag/su/de) — confirm this reads clearly as a
    // table-header abbreviation.
    lossesHeader: "Pérdidas (ag/su/de)",
    // #396 — machine-drafted, pending native review (#182).
    conditionHeader: "Con defecto",
    mortalityHeader: "Mortalidad",
    gradedHeader: "Clasificado",
    editButton: "editar",
    adjustButton: "ajustar",
    voidButton: "anular",
    loadMoreButton: "cargar más",

    // Entry-status pills
    statusVoided: "Anulado",
    statusAdjusted: "Ajustado",
    statusLocked: "Bloqueado",
    lockedAt: "Bloqueado {{time}}",
    statusSubmitted: "Presentado",
    statusDraft: "Borrador",
  },

  // machine-drafted (#182) — pending native review. Task 28 (B5): new
  // namespace, backfilling es so Spanish mode renders translated text on the
  // Reports screen. Keys mirror en.ts reports exactly, including the
  // {{count}}/{{revenue}}/{{paid}}/{{outstanding}}/{{total}}/{{expenses}}/
  // {{profit}} placeholders and the <strong> tag in profitLine.
  reports: {
    title: "Informes",
    fromLabel: "Desde",
    toLabel: "Hasta",

    productionHeading: "Producción",
    dateHeader: "Fecha",
    eggsHeader: "Huevos",
    lossesHeader: "Pérdidas (ag/su/de)",
    sellableHeader: "Vendible",
    // #396 — huevos rajados/sucios que pasaron a inventario en vez de perderse.
    // Machine-drafted, pending native review (#182).
    conditionHeader: "Con defecto",
    deathsHeader: "Muertes",
    henDaysHeader: "Días-gallina",
    henDayPctHeader: "% días-gallina",
    periodRowLabel: "Período",
    gradeTotalsLabel: "Por grado:",

    moneyHeading: "Dinero",
    salesRowLabel: "Ventas",
    salesSummary:
      "{{count}} pedido(s) confirmado(s) — ingresos {{revenue}}, pagado {{paid}}, pendiente {{outstanding}}",
    salesVoidedSuffix: " ({{count}} anulado(s))",
    expensesRowLabel: "Gastos",
    expensesNone: "sin registrar",
    expensesTotalSuffix: " — total {{total}}",
    profitRowLabel: "Ganancia (básica)",
    profitLine: "ingresos {{revenue}} − gastos {{expenses}} = <strong>{{profit}}</strong>",
    profitFootnote:
      "La ganancia \"básica\" es el ingreso confirmado menos los gastos "
      + "registrados — sin costo de bienes vendidos ni valoración de inventario.",
  },

  // machine-drafted (#182) — pending native review. Task 29 (B5): new
  // namespace, backfilling es so Spanish mode renders translated text on the
  // Audit screen. Keys mirror en.ts audit exactly (no placeholders in this
  // namespace — the action/entity table cells route through the already-
  // translated enums:auditAction.*/entityType.* labels, not this namespace).
  audit: {
    heading: "Registro de auditoría",
    intro:
      "Todo cambio correctivo, destructivo o de configuración — quién lo "
      + "hizo, cuándo y por qué. Las filas se escriben junto con el cambio "
      + "mismo y nunca se editan.",
    actionFilterLabel: "Acción",
    allActionsOption: "Todas las acciones",
    whenHeader: "Cuándo (UTC)",
    whoHeader: "Quién",
    actionHeader: "Acción",
    entityHeader: "Entidad",
    reasonHeader: "Motivo",
    emptyMessage: "Aún no hay eventos de auditoría.",
    loadMoreButton: "cargar más",
  },

  // machine-drafted (#182) — pending native review. Task 30 (B5): new
  // namespace, backfilling es so Spanish mode renders translated text on the
  // Export screen. Keys mirror en.ts export exactly (no placeholders in this
  // namespace). The "dataset.<slug>" keys are DISPLAY labels for the dataset
  // picker (the wire value stays the raw slug) — translated to natural
  // Spanish per the task brief.
  export: {
    heading: "Exportar",
    intro:
      "Descargue los datos de su cuenta como archivos CSV — una copia de "
      + "seguridad manual que puede guardar donde quiera. Los valores "
      + "monetarios se exportan en unidades menores (centavos) con su "
      + "moneda, exactamente como se almacenan.",

    fullBackupHeading: "Copia de seguridad completa",
    fullBackupButton: "Descargar copia de seguridad completa (zip)",
    fullBackupHint: "Un zip con todos los conjuntos de datos a continuación más un manifiesto de recuentos de filas.",
    preparingButton: "Preparando…",

    singleDatasetsHeading: "Conjuntos de datos individuales",

    "dataset.flocks": "Parvadas",
    "dataset.bird-movements": "Movimientos de aves",
    "dataset.daily-entries": "Entradas diarias",
    "dataset.daily-entry-grades": "Grados de entradas diarias",
    "dataset.egg-grades": "Grados de huevo",
    "dataset.egg-lots": "Lotes de huevo",
    "dataset.customers": "Clientes",
    "dataset.sales-orders": "Pedidos de venta",
    "dataset.sales-order-items": "Artículos de pedidos de venta",
    "dataset.sales-order-allocations": "Asignaciones de pedidos de venta",
    "dataset.payments": "Pagos",
    "dataset.inventory-items": "Artículos de inventario",
    "dataset.inventory-lots": "Lotes de inventario",
    "dataset.inventory-movements": "Movimientos de inventario",
    "dataset.feed-usages": "Usos de alimento",
    "dataset.water-usages": "Usos de agua",
    "dataset.expense-categories": "Categorías de gastos",
    "dataset.expenses": "Gastos",
    "dataset.egg-inventory-movements": "Movimientos de inventario de huevo",
    "dataset.audit-events": "Eventos de auditoría",
  },

  // machine-drafted (#182) — pending native review. Task 25c (B4): new
  // namespace, backfilling es for the closed-vocabulary enum labels
  // (status/role/waterSource/waterUnit/gradeType/inventoryCategory/
  // inventoryMovement/flockMovement/stockMovement/unitSystem/weekday)
  // consumed through enums.ts, so Settings' unit-system/weekday pickers and
  // Users' role values render translated text too. Keys mirror en.ts enums
  // exactly (flat "family.RawValue" strings, keySeparator:false — see
  // en.ts's enums header comment). waterUnit.L/waterUnit.gal are left as the
  // literal unit symbols (unchanged across en/es/tl), matching en's own
  // comment.
  enums: {
    // status
    "status.Active": "Activo",
    "status.Inactive": "Inactivo",
    "status.Draft": "Borrador",
    "status.Submitted": "Presentado",
    "status.Locked": "Bloqueado",
    "status.ManagerAdjusted": "Ajustado",
    "status.Voided": "Anulado",
    "status.Confirmed": "Confirmado",
    "status.Shipped": "Enviado",
    "status.Invoiced": "Facturado",
    "status.Cancelled": "Cancelado",
    "status.Depleted": "Agotado",
    "status.Archived": "Archivado",

    // role
    "role.Worker": "Trabajador",
    "role.Admin": "Administrador",
    "role.Manager": "Gerente",
    "role.Sales": "Ventas",
    "role.ReadOnly": "Solo lectura",

    // water source
    "waterSource.Well": "Pozo",
    "waterSource.Municipal": "Municipal",
    "waterSource.Tank": "Tanque",
    "waterSource.Other": "Otro",

    // water unit — symbols, unchanged
    "waterUnit.L": "L",
    "waterUnit.gal": "gal",

    // grade type
    "gradeType.Size": "Tamaño",
    "gradeType.Quality": "Calidad",
    "gradeType.Custom": "Personalizado",

    // inventory category
    "inventoryCategory.Feed": "Alimento",
    "inventoryCategory.Supplement": "Suplemento",
    "inventoryCategory.Additive": "Aditivo",
    "inventoryCategory.Medication": "Medicamento",
    "inventoryCategory.Vaccine": "Vacuna",
    "inventoryCategory.Packaging": "Empaque",
    "inventoryCategory.Bedding": "Cama",
    "inventoryCategory.Sanitation": "Saneamiento",
    "inventoryCategory.EquipmentPart": "Pieza de equipo",
    "inventoryCategory.Other": "Otro",

    // inventory movement type
    "inventoryMovement.Purchase": "Compra",
    "inventoryMovement.Usage": "Uso",
    "inventoryMovement.Adjustment": "Ajuste",
    "inventoryMovement.Discard": "Desecho",

    // flock (bird) movement type
    "flockMovement.Mortality": "Mortalidad",
    "flockMovement.Cull": "Descarte",
    "flockMovement.Adjustment": "Ajuste",

    // egg stock movement type
    "stockMovement.Production": "Producción",
    "stockMovement.Sale": "Venta",
    "stockMovement.Adjustment": "Ajuste",
    "stockMovement.Discard": "Desecho",
    "stockMovement.InternalUse": "Uso interno",
    "stockMovement.Transfer": "Transferencia",
    "stockMovement.Reconciliation": "Conciliación",
    "stockMovement.Void": "Anulado",

    // unit system
    "unitSystem.Metric": "Métrico",
    "unitSystem.Imperial": "Imperial",

    // weekday
    "weekday.Sunday": "Domingo",
    "weekday.Monday": "Lunes",
    "weekday.Tuesday": "Martes",
    "weekday.Wednesday": "Miércoles",
    "weekday.Thursday": "Jueves",
    "weekday.Friday": "Viernes",
    "weekday.Saturday": "Sábado",

    // machine-drafted (#182) — pending native review. Task 29 (B5): audit
    // action + entity type labels for AuditPage. Keys mirror en.ts exactly
    // (flat "auditAction.Entity.Verb" / "entityType.Value" strings,
    // keySeparator:false — see en.ts's auditAction header comment).
    "auditAction.DailyEntry.Adjust": "Entrada diaria ajustada",
    "auditAction.DailyEntry.Void": "Entrada diaria anulada",
    "auditAction.SalesOrder.Void": "Pedido de venta anulado",
    "auditAction.Payment.Void": "Pago anulado",
    "auditAction.Expense.Adjust": "Gasto ajustado",
    "auditAction.ExpenseCategory.Update": "Categoría de gasto actualizada",
    "auditAction.InventoryItem.Adjust": "Artículo de inventario ajustado",
    "auditAction.WaterUsage.Correct": "Uso de agua corregido",
    "auditAction.Flock.BirdMovement": "Movimiento de aves registrado",
    "auditAction.Flock.Update": "Lote actualizado",
    "auditAction.Flock.Deplete": "Lote agotado",
    "auditAction.Flock.Archive": "Lote archivado",
    "auditAction.Flock.Reactivate": "Lote reactivado",
    "auditAction.EggGrade.Update": "Grado de huevo actualizado",
    "auditAction.EggGrade.Activate": "Grado de huevo activado",
    "auditAction.EggGrade.Deactivate": "Grado de huevo desactivado",
    "auditAction.User.Create": "Usuario creado",
    "auditAction.User.Update": "Usuario actualizado",
    "auditAction.User.PasswordSet": "Contraseña establecida",
    "auditAction.User.PasswordChanged": "Contraseña cambiada",
    "auditAction.User.BreakGlassReset": "Restablecimiento de emergencia",
    "auditAction.User.FlockAssign": "Lote asignado al usuario",
    "auditAction.User.FlockUnassign": "Lote desasignado del usuario",
    "auditAction.Account.Export": "Datos exportados",
    "auditAction.Account.SetLogo": "Logotipo de la granja establecido",
    "auditAction.Account.RemoveLogo": "Logotipo de la granja eliminado",
    "auditAction.Account.UpdateSettings": "Configuración de la granja actualizada",
    "auditAction.Product.Create": "Producto creado",
    "auditAction.Product.Update": "Producto actualizado",
    "auditAction.Product.Activate": "Producto activado",
    "auditAction.Product.Deactivate": "Producto desactivado",
    "auditAction.EggUnitConversion.Update": "Conversión de unidad de huevo actualizada",

    "entityType.Account": "Cuenta",
    "entityType.DailyEntry": "Entrada diaria",
    "entityType.EggGrade": "Grado de huevo",
    "entityType.EggUnitConversion": "Conversión de unidad de huevo",
    "entityType.Expense": "Gasto",
    "entityType.ExpenseCategory": "Categoría de gasto",
    "entityType.FarmLogo": "Logotipo de la granja",
    "entityType.Flock": "Lote",
    "entityType.InventoryItem": "Artículo de inventario",
    "entityType.Payment": "Pago",
    "entityType.Product": "Producto",
    "entityType.SalesOrder": "Pedido de venta",
    "entityType.User": "Usuario",
    "entityType.WaterUsage": "Uso de agua",
  },

  // machine-drafted (#182) — pending native review. Task 32 (B6a): HelpPage
  // prose, getting-around through mistakes (INCLUDING the "Fixing mistakes"
  // table). Keys mirror en.ts help exactly, including every <strong>/<em> tag
  // — catalogParity enforces tag parity per key. Formal "usted" register,
  // matching the rest of this pack. Screen/feature names referenced in prose
  // (Entrada diaria, Historial, Ventas, etc.) are translated here, unlike the
  // older "kept in English" precedent noted at the top of this file — every
  // screen HelpPage references is already in TRANSLATED_NAMESPACES by now.
  // The glossary table (h3 id="glossary") is translated separately, near the
  // end of this block (Task 33, B6b) — tocGlossary above is just the rail's
  // link text.
  help: {
    eyebrow: "Guía del usuario",
    heading: "Ayuda",
    lead: "Cómo funciona Cluckwork, pantalla por pantalla — y cómo deshacer errores.",
    contentsAriaLabel: "Contenido de la ayuda",
    contentsEyebrow: "Contenido",

    tocGettingAround: "Cómo moverse",
    tocSigningIn: "Iniciar sesión",
    tocDailyLoop: "El ciclo diario",
    tocRoles: "Quién puede hacer qué",
    tocDialogs: "Agregar y corregir",
    tocDailyEntry: "Entrada diaria",
    tocFlocks: "Lotes y aves",
    tocGrades: "Grados de huevo",
    tocProducts: "Productos",
    tocStock: "Existencias",
    tocInventory: "Suministros e inventario",
    tocFeed: "Alimento",
    tocWater: "Agua",
    tocSales: "Clientes y ventas",
    tocReports: "Informes",
    tocExpenses: "Gastos",
    tocHistory: "Historial",
    tocAudit: "Registro de auditoría",
    tocExport: "Exportar y respaldo",
    tocFarmSettings: "Configuración de la granja",
    tocFarmPalette: "Paleta de la granja",
    tocAccount: "Su cuenta",
    tocInstall: "Instalar en un teléfono",
    tocMistakes: "Corregir errores",
    tocGlossary: "Glosario",

    gettingAroundHeading: "Cómo moverse",
    gettingAroundSidebar:
      "En una computadora, cada pantalla está en la <strong>barra lateral</strong> de la izquierda, agrupada "
      + "por función.",
    gettingAroundTabs:
      "En un teléfono, las pantallas que más usa aparecen como <strong>pestañas en la parte "
      + "inferior</strong>, al alcance del pulgar. Cuáles cuatro obtiene depende de su rol — un trabajador "
      + "obtiene Entrada diaria, alguien de ventas obtiene Ventas. Todo lo demás está a un toque de "
      + "distancia bajo <strong>Más</strong>.",
    gettingAroundErrorScreen:
      "Si una pantalla alguna vez muestra <strong>\"Algo salió mal\"</strong>, eso es la aplicación "
      + "capturando un error en lugar de dejarlo en una página en blanco. Todo lo que ya había guardado "
      + "está a salvo (lo que todavía estaba escribiendo puede que deba ingresarlo de nuevo) — toque "
      + "<strong>Recargar</strong>, o <strong>Volver al panel</strong> e inténtelo de nuevo. Si sigue "
      + "sucediendo, abra \"Detalles del error\" y envíe una captura de pantalla.",

    signingInHeading: "Iniciar sesión",
    signingInBasic:
      "Inicie sesión con el correo electrónico y la contraseña que configuró su administrador. Una "
      + "contraseña incorrecta simplemente indica <strong>Correo electrónico o contraseña inválidos</strong> "
      + "— inténtelo de nuevo.",
    signingInRateLimit:
      "Para frenar a cualquiera que intente adivinar contraseñas, los intentos de inicio de sesión desde "
      + "el mismo lugar están <strong>limitados</strong>. Después de demasiados intentos en pocos minutos "
      + "verá <strong>\"Demasiados intentos de inicio de sesión\"</strong> — eso no es una falla, solo "
      + "espere unos minutos e inténtelo de nuevo. Estar <em>ya conectado</em> nunca se ve afectado; su "
      + "trabajo continúa con normalidad.",
    signingInAccountLock:
      "Por separado, demasiadas contraseñas incorrectas para <em>una cuenta</em> bloquean brevemente "
      + "<em>esa</em> cuenta. Mientras está bloqueada, incluso la contraseña correcta sigue indicando "
      + "<strong>Correo electrónico o contraseña inválidos</strong>. El bloqueo es temporal — espere hasta "
      + "unos 15 minutos e inténtelo de nuevo.",
    signingInPersistence:
      "Su inicio de sesión se guarda de forma segura en su navegador y permanece activo mientras trabaja, "
      + "incluso al recargar y con la aplicación abierta en <strong>varias pestañas</strong> a la vez. "
      + "Después de que la aplicación se <strong>actualiza</strong> es posible que se le pida iniciar "
      + "sesión una vez más — eso es normal.",
    // machine-drafted (#393) — pending native review.
    signingInMultiTabResync:
      "Iniciar sesión como otra persona en una <strong>pestaña del navegador</strong> mientras otra pestaña "
      + "del mismo navegador está a mitad de su propia verificación silenciosa puede, en ocasiones, cerrarle "
      + "la sesión justo después — simplemente vuelva a iniciar sesión. Esto solo ocurre en ese momento "
      + "puntual con varias pestañas y nunca pierde nada que ya haya guardado.",
    // machine-drafted (#283) — pending native review.
    signingInFirstRun:
      "<strong>Primer inicio de sesión en una granja nueva.</strong> No hay una contraseña predeterminada — "
      + "un operador ejecuta un comando de configuración único que imprime una contraseña temporal. Inicie "
      + "sesión con ella y llegará de inmediato a una pantalla de <strong>Establecer su contraseña</strong> en "
      + "lugar de la aplicación normal; nada más funciona hasta que elija su propia contraseña allí. Esto es "
      + "distinto de un <em>Cambio de contraseña</em> ordinario. Hasta que se realice ese paso de "
      + "configuración, al intentar iniciar sesión se le indica esto y se le remite a quien administra el "
      + "servidor, en lugar de afirmar que sus datos eran incorrectos.",
    // #308 (machine-drafted, pending native review)
    signingInStepUp:
      "Dos acciones en la pantalla <strong>Usuarios</strong> le piden <strong>volver a ingresar su contraseña "
      + "actual</strong> directamente en el diálogo: crear otro propietario y restablecer la contraseña de un "
      + "propietario existente. Esto confirma que realmente es usted antes de otorgar tanto acceso — ninguna "
      + "otra acción en esa pantalla (crear un usuario Worker/Manager/Sales/Read-only, restablecer su "
      + "contraseña) vuelve a preguntar.",
    signingInCredentialEpoch:
      "Cuando un administrador restablece una contraseña, su sesión actual puede invalidarse inmediatamente. Si "
      + "ve un mensaje indicando que sus credenciales cambiaron, inicie sesión de nuevo con su contraseña actual.",
    interfaceLanguage:
      "<strong>Idioma de la interfaz.</strong> Cualquiera puede elegir el idioma en que se muestra la "
      + "interfaz desde <strong>Cuenta → Preferencias</strong> — inglés, español o tagalo. La traducción es "
      + "un trabajo en progreso: las pantallas de inicio de sesión y ventas, los mensajes de error, y "
      + "<strong>Cuenta → Preferencias</strong> mismo están traducidos hoy; el resto de la pantalla de "
      + "Cuenta (incluida la sección de contraseña) y el resto de la aplicación se están traduciendo "
      + "pantalla por pantalla. Hasta que una pantalla esté traducida, simplemente se muestra en inglés, "
      + "sea cual sea el idioma que eligió.",

    dailyLoopHeading: "El ciclo diario",
    dailyLoopChain:
      "Todo en Cluckwork depende de una sola cadena: usted registra una <strong>entrada diaria</strong> por "
      + "cada lote (huevos por grado, pérdidas, muertes), la <strong>envía</strong>, y al enviarla se crean "
      + "<strong>lotes de huevos</strong> fechados — esas son sus <strong>existencias</strong> vendibles. Un "
      + "<strong>pedido de venta</strong> toma de las existencias al confirmarlo, siempre los huevos más "
      + "antiguos primero. El alimento fluye de la misma manera del lado de entrada: las compras ingresan "
      + "alimento a las existencias, el uso diario las reduce por lote.",
    dailyLoopSummary: "Registrar entrada → enviar → lotes de huevos → existencias → pedido → confirmar.",

    rolesHeading: "Quién puede hacer qué",
    rolesWorkers:
      "Cinco tipos de inicio de sesión. Los <strong>Trabajadores</strong> llevan a cabo el ciclo diario — "
      + "registran y envían entradas, reciben alimento, registran el uso de alimento y agua, crean lotes y "
      + "clientes, gestionan pedidos desde borrador hasta confirmación. Un trabajador puede restringirse a "
      + "<strong>lotes asignados</strong>: sin asignaciones puede registrar para cualquier lote; la primera "
      + "asignación lo restringe a los indicados.",
    rolesManagers:
      "Los <strong>Gerentes</strong> hacen todo lo que hacen los trabajadores, además de todo lo que "
      + "<strong>deshace, corrige o configura</strong>: anulaciones, correcciones de existencias y agua, "
      + "ciclo de vida de los lotes, sacrificios, los catálogos de grados/productos/artículos, gastos, "
      + "informes de dinero, el registro de auditoría y las exportaciones.",
    rolesSalesReadOnly:
      "Los inicios de sesión de <strong>Ventas</strong> gestionan clientes, pedidos y <strong>pagos</strong> "
      + "— pero no captura de producción ni gastos. Los inicios de sesión de <strong>Solo lectura</strong> "
      + "ven existencias, historial e informes, y no pueden cambiar nada.",
    rolesAdmin:
      "<strong>Administrador (propietario)</strong> hace todo lo que hace un gerente y es el único rol que "
      + "administra usuarios: crea inicios de sesión en la pantalla <strong>Usuarios</strong> (correo "
      + "electrónico, contraseña, un nombre opcional y rol) y asigna trabajadores a lotes. El nombre de un "
      + "usuario se puede cambiar más tarde desde la acción <strong>editar</strong> de la fila, y la acción "
      + "<strong>contraseña</strong> establece una contraseña olvidada sin necesitar la anterior. Cambiar el "
      + "rol de un usuario existente llega con una versión posterior. Los controles que no puede usar "
      + "están ocultos, y el servidor los rechaza de todos modos.",
    ownPassword:
      "<strong>Su propia contraseña.</strong> Cualquiera, en cualquier rol, puede cambiar su propia "
      + "contraseña en la pantalla <strong>Cuenta</strong> ingresando la actual y una nueva (al menos 12 "
      + "caracteres). Cambiar su propia contraseña mantiene este dispositivo conectado con credenciales "
      + "nuevas y termina cada <em>otra</em> sesión abierta en su siguiente solicitud. Si un administrador le "
      + "establece la contraseña, todas sus sesiones abiertas terminan en su siguiente solicitud.",

    dialogsHeading: "Agregar y corregir",
    dialogsPopup:
      "Agregar y corregir ocurren en una ventana emergente. Busque el botón <strong>Nuevo …</strong> junto "
      + "al título de la pantalla — nuevo grado, producto, cliente, lote, artículo, usuario, pedido. El "
      + "enlace <strong>editar</strong> o <strong>corregir</strong> de cada fila abre la misma ventana "
      + "emergente con los valores de esa fila ya completados.",
    dialogsDrillDowns:
      "Las vistas detalladas funcionan de la misma manera. Abra el libro mayor de <strong>aves</strong> de "
      + "un lote para registrar un sacrificio, un artículo de inventario para registrar una compra, uso de "
      + "alimento o una corrección de existencias, un pedido para <strong>registrar un pago</strong>, o los "
      + "<strong>lotes</strong> de un trabajador para gestionar su acceso — el libro mayor permanece donde "
      + "está y el formulario viene a usted.",
    dialogsCancel:
      "<strong>Cancelar</strong>, Escape, o un clic fuera cierra la ventana emergente, no registra nada y "
      + "borra lo que escribió — al volver a abrirla empieza con un formulario en blanco. Si un guardado "
      + "falla, la ventana permanece abierta con sus valores y el motivo, para que pueda corregirlo e "
      + "intentarlo de nuevo — reintentar es seguro, nunca registra lo mismo dos veces.",
    dialogsInlineForms:
      "Las pantallas cuyo único trabajo es la captura mantienen su formulario en la página: <strong>Entrada "
      + "diaria</strong>, <strong>Agua</strong>, registrar un gasto, y agregar líneas a un pedido en "
      + "borrador. Las que usa todos los días — sin clic adicional.",
    dialogsSteppers:
      "Los conteos de números enteros — conteos de huevos, conteos de aves, cantidades de venta, huevos por "
      + "unidad — tienen botones <strong>−</strong> y <strong>+</strong> del tamaño del pulgar: toque para "
      + "uno, <strong>mantenga presionado</strong> para acelerar. La cantidad de una línea de venta nunca "
      + "baja de 1 y siempre es un <strong>número entero</strong>, ya sea con los botones o escrita. Los "
      + "decimales van en los precios, que se escriben.",
    dialogsConfirm:
      "<strong>Las acciones que no se pueden deshacer preguntan primero.</strong> Enviar un día, confirmar "
      + "o cancelar un pedido, agotar o archivar un lote — cada una indica lo que está por suceder y "
      + "espera. El teclado comienza en <strong>Cancelar</strong>, para que presionar Enter por costumbre "
      + "nunca lo confirme. Un botón <strong>rojo</strong> significa que la acción deshace o retira algo: "
      + "anular, cancelar un borrador, agotar, archivar. Enviar un día y confirmar un pedido tampoco se "
      + "pueden deshacer, pero son la forma normal de avanzar en la semana.",
    dialogsVoidReason:
      "<strong>Las anulaciones necesitan un motivo.</strong> Anular una entrada diaria, un pago o un "
      + "pedido confirmado pregunta de la misma manera pero primero solicita un motivo por escrito — se "
      + "guarda con la anulación y se muestra dondequiera que ese registro aparezca después, así que "
      + "escriba lo que realmente sucedió. Déjelo vacío y la ventana emergente lo indica en el momento, "
      + "conservando lo que haya escrito.",

    dailyEntryHeading: "Entrada diaria",
    dailyEntryPanes:
      "Elija el lote y la fecha en la parte superior, luego trabaje en dos paneles uno al lado del otro: "
      + "<strong>1 Conteo de huevos</strong> (total, rotos, sucios, descartados, muertes) y <strong>2 "
      + "Clasificación</strong>. Los conteos producen una cifra <strong>vendible</strong>, y ese es el "
      + "número al que deben sumar los grados. Un borrador puede quedar parcialmente clasificado, o sin "
      + "clasificar — para enviar, debe cuadrar exactamente.",
    dailyEntryGradingDown:
      "La clasificación cuenta <strong>hacia abajo</strong>. Junto a los grados se muestra cuántos huevos "
      + "vendibles le quedan por ubicar; se pone verde en cuanto el día cuadra y rojo si se excede — "
      + "excederse también bloquea guardar el borrador, no solo Enviar. No puede enviar hasta que llegue "
      + "exactamente a cero — clasificar el día a medias, o no clasificarlo, está bien para un borrador, "
      + "pero no para enviar.",
    dailyEntryButtons:
      "Cada conteo tiene botones <strong>−</strong> y <strong>+</strong>. Toque para uno, o <strong>mantenga "
      + "presionado</strong> — se acelera a medida que avanza, así que unos cientos de huevos toman "
      + "aproximadamente un segundo. Más fácil que un teclado numérico con guantes puestos. El "
      + "<strong>+</strong> de un grado ya no se detiene en el total actual del día — cuente los grados "
      + "primero y el total se ajusta para igualarlos. Solo aumenta el total, nunca lo reduce, así que "
      + "recortar el total en el paso 1 nunca empuja un grado hacia abajo. Las granjas que cuentan por "
      + "bandeja pueden hacer que cada toque cuente una unidad de empaque completa en lugar de un huevo — "
      + "el valor predeterminado de la granja está en <strong>Configuración</strong>, y cada persona puede "
      + "elegir el suyo en su pantalla de <strong>Cuenta</strong>. Cuando una unidad de empaque está en uso, "
      + "los propios botones lo indican (<strong>−30 / +30</strong>) y una nota sobre los paneles nombra la "
      + "unidad; escribir sigue ingresando números exactos.",
    dailyEntryPutAllIn:
      "La mayoría de los días terminan de la misma manera — un grado se lleva lo que queda. <strong>Poner "
      + "todo en…</strong> junto al conteo restante lo hace en un solo movimiento: arrástrelo a un grado, o "
      + "tóquelo y elija uno.",
    dailyEntrySaveBar:
      "Ambos botones de guardar permanecen en una barra en la parte inferior de la pantalla mientras se "
      + "desplaza. En un teléfono esa barra también muestra el conteo vendible y cuántos quedan, para que "
      + "nunca pierda de vista si el día cuadra.",
    dailyEntrySaveSubmit:
      "<strong>Guardar borrador</strong> mantiene el día editable. <strong>Enviar</strong> lo hace oficial: "
      + "crea los lotes de huevos del día y registra las muertes en el libro mayor de aves del lote. Los "
      + "trabajadores ya no pueden editarlo — un administrador puede ajustarlo o anularlo (vea \"Corregir "
      + "errores\").",
    dailyEntryLocking:
      "Las entradas enviadas se <strong>bloquean automáticamente después de 7 días</strong>. Bloqueada "
      + "solo significa que la ventana de corrección para arreglos rutinarios ha pasado — el "
      + "ajuste/anulación por parte de un administrador aún funciona en entradas bloqueadas.",
    dailyEntryToday:
      "\"Hoy\" significa <strong>el hoy de su granja</strong>, no el reloj de alguna otra parte del mundo. "
      + "Puede registrar cualquier día hasta e incluyendo ese; un día que aún no ha ocurrido en la granja "
      + "se rechaza — aquí y en todos los demás lugares donde ingresa una fecha: uso de alimento y agua, "
      + "compras de alimento y correcciones de existencias, gastos, pagos, y la fecha de colocación de un "
      + "lote. La misma fecha decide cuándo los huevos salen de un período de retiro, qué huevos puede "
      + "tomar una venta, el día en que un lote se agota o archiva, y el rango en que se abren los informes "
      + "— para que nada discrepe sobre qué día es.",
    dailyEntryOnePerDay:
      "Una entrada por lote por día. Reabrir un día que tiene un borrador lo carga para edición y muestra "
      + "una insignia de <strong>Editando borrador</strong> junto al título, para que retomar el trabajo "
      + "guardado nunca parezca empezar de cero. Si el precargado falla, guardar se bloquea hasta que tenga "
      + "éxito, para que un borrador existente nunca se sobrescriba en silencio.",
    dailyEntryDepletedBackfill:
      "Los lotes agotados aceptan entradas retroactivas hasta su fecha de agotamiento; los lotes "
      + "archivados no aceptan ninguna.",

    flocksHeading: "Lotes y aves",
    flocksCurrentBirds:
      "Las <strong>aves actuales</strong> de un lote = su conteo inicial menos todo lo registrado en su "
      + "<strong>libro mayor de aves</strong>: muertes (agregadas automáticamente al enviar entradas), "
      + "<strong>sacrificios</strong> (aves retiradas deliberadamente — vendidas, sacrificadas, regaladas), "
      + "y <strong>ajustes</strong> manuales (correcciones de conteo, en cualquier dirección).",
    flocksLifecycle:
      "Ciclo de vida: <strong>Activo</strong> (normal) → <strong>Agotado</strong> (aves ya no quedan; el "
      + "historial permanece, se permite retroactivo) → <strong>Archivado</strong> (oculto del trabajo "
      + "diario). Agotar y archivar piden confirmación; ambos son reversibles con <strong>Reactivar</strong>.",
    flocksPermissions:
      "Cualquiera puede crear un lote y ver el libro mayor de aves. Editar un lote, los cambios de ciclo "
      + "de vida y registrar sacrificios/ajustes son solo para administradores.",

    gradesHeading: "Grados de huevo",
    gradesBuckets:
      "Los grados son las categorías de clasificación de su granja — tamaños (Grande…), calidades "
      + "(Roto…), o personalizados. Solo los grados <strong>vendibles</strong> aparecen en la captura de "
      + "entradas y en los pedidos; las categorías no vendibles son de contabilidad.",
    gradesDeactivating:
      "Los grados nunca se eliminan. <strong>Desactivar</strong> elimina un grado de la captura y de los "
      + "selectores de pedidos: sus existencias siguen contándose y las líneas de pedido agregadas antes "
      + "aún pueden confirmarse, pero no se puede colocar en líneas de pedido <em>nuevas</em> — reactive el "
      + "grado para vender las existencias restantes. El historial sigue mostrando su nombre.",
    gradesAdminOnly: "El catálogo de grados es configuración — administrarlo es solo para administradores.",

    productsHeading: "Productos (admin)",
    productsWhatYouSell:
      "Los productos son lo que vende — \"Huevos Grandes por docena\", \"Cartón mixto\". Cada producto de "
      + "huevo apunta a un grado de huevo (de ahí vienen sus existencias) y lleva una unidad de venta y un "
      + "precio predeterminado opcional. Por ahora solo existen productos de huevo.",
    productsPackedUnits:
      "Las <strong>unidades empacadas</strong> establecen cuántos huevos contiene cada unidad — su cartón "
      + "podría ser de 12, 18 o 30. Cambiar una unidad solo afecta las ventas futuras; los pedidos pasados "
      + "conservan el conteo con el que se vendieron.",

    stockHeading: "Existencias",
    stockLots:
      "Cada grado se expande en sus <strong>lotes</strong> (uno por día enviado), y cada lote en su "
      + "<strong>libro mayor de movimientos</strong> — una línea explícita por cada producción, venta, "
      + "corrección o anulación. La suma acumulada siempre es igual al saldo mostrado; nada cambia las "
      + "existencias sin dejar una línea.",
    stockRestricted:
      "Las existencias son la suma de sus lotes de huevos por grado. La columna <strong>restringido</strong> "
      + "está reservada para períodos de retiro por medicación — esa función llega con el seguimiento de "
      + "medicación. <strong>Nada marca huevos como restringidos todavía, así que el sistema no impone "
      + "tiempos de retiro hoy</strong> — gestione los períodos de retiro fuera de Cluckwork por ahora.",
    stockFifo: "Vender siempre toma primero los lotes más antiguos, para que las existencias roten naturalmente.",

    inventoryHeading: "Alimento e inventario",
    inventoryItems:
      "Los <strong>artículos</strong> definen lo que rastrea (alimento, suplementos…) y la unidad en que se "
      + "mide. La unidad se bloquea una vez que se han recibido existencias — las cantidades registradas "
      + "deben seguir significando lo que significaban.",
    inventoryPurchaseUsage:
      "<strong>Registrar compra</strong> asienta las existencias recibidas como un lote fechado con su "
      + "costo. La alimentación de una parvada se registra en la <strong>página de Alimento</strong> — el "
      + "panel de un artículo alimentable enlaza directamente allí con el artículo preseleccionado.",
    inventoryLedger:
      "Cada cambio queda registrado en el <strong>libro mayor de movimientos</strong> del artículo — "
      + "compras, uso, correcciones. Las filas del libro mayor nunca se editan ni se eliminan.",
    inventoryCorrections:
      "Los errores tipográficos y el deterioro se corrigen con <strong>correcciones</strong>: un "
      + "<em>Ajuste</em> (en cualquier dirección) o un <em>Descarte</em> (baja) contra un lote específico, "
      + "siempre con un motivo. La fila original y la corrección permanecen visibles.",
    inventoryPermissions:
      "Registrar compras y uso está abierto para todos; el catálogo de artículos y las correcciones de "
      + "existencias son solo para administradores.",

    feedHeading: "Alimento",
    feedRecording:
      "<strong>Registrar alimento</strong> anota lo que comió una parvada en un día: elija la parvada, el "
      + "artículo (las existencias actuales se ven en el propio selector), la cantidad y la fecha. Las "
      + "existencias se descuentan de las compras más antiguas primero — solo lotes que existían en esa "
      + "fecha — y el costo estimado proviene de los lotes realmente consumidos. El historial de la página "
      + "lista cada alimentación con su costo estimado.",
    feedCorrecting:
      "Los registros de alimento <strong>nunca se editan</strong>: las existencias que consumieron ya están "
      + "en el libro mayor, así que un error se corrige con un <strong>ajuste</strong> de Inventario en el "
      + "lote afectado (con motivo), que queda visible junto al original.",
    feedDailyEntry:
      "La página de <strong>Entrada diaria</strong> muestra de un vistazo el alimento y el agua del día de "
      + "la parvada seleccionada, con enlace aquí. Un registro de alimento o agua hecho cuando ya existe la "
      + "entrada de ese día también recuerda esa entrada — los hechos antes quedan sin enlazar a propósito; "
      + "el día en sí es lo que los une.",
    waterHeading: "Agua",
    waterRecording:
      "Registre lo que bebió cada lote por día: una cantidad directa (litros o galones) o <strong>lecturas "
      + "de medidor</strong> — la cantidad es entonces la diferencia del medidor (final − inicial).",
    waterCorrecting:
      "Los registros de agua no tienen existencias detrás, así que los errores se corrigen "
      + "<strong>corrigiendo el registro directamente</strong> (el botón \"corregir\", solo para "
      + "administradores) — sin asientos de compensación. El lote y la fecha son fijos: si se eligió mal, "
      + "regístrelo de nuevo bajo el correcto.",
    waterLifecycle:
      "Misma regla de ciclo de vida que en todas partes: los lotes agotados aceptan retroactivos hasta su "
      + "fecha de agotamiento, los archivados no aceptan nada.",

    salesHeading: "Clientes y ventas",
    salesDrafts:
      "Los pedidos comienzan como <strong>borradores</strong>: agregue líneas eligiendo un "
      + "<strong>producto</strong>, una unidad empacada (docena, cartón, …), una cantidad entera, y un "
      + "precio por unidad (precargado desde el valor predeterminado del producto, se permiten decimales) — "
      + "edite libremente, o <strong>cancele</strong> (el borrador se conserva, de solo lectura). La cantidad "
      + "cuenta <strong>unidades, no huevos</strong> — el campo indica la unidad y muestra el total de huevos "
      + "resultante mientras escribe (2 bandejas = 60 huevos, no 60 bandejas). Cada línea "
      + "recuerda cuántos huevos contenía su unidad cuando se agregó, así que redefinir un cartón más tarde "
      + "nunca cambia pedidos antiguos.",
    salesConfirming:
      "<strong>Confirmar</strong> un pedido asigna existencias reales — los lotes más antiguos primero — y "
      + "es el punto donde el inventario cambia de manos.",
    salesVoiding:
      "Una confirmación errónea se deshace con <strong>Anular</strong> (solo administrador, motivo "
      + "requerido): los huevos regresan exactamente a los lotes de donde vinieron, y el pedido queda "
      + "listado como Anulado. Anular es para errores, no para devoluciones de mercancía entregada. (Los "
      + "pedidos confirmados antes de que existiera el seguimiento de asignación a nivel de lote no pueden "
      + "anularse por su cuenta — consulte a su administrador.)",
    salesPayments:
      "<strong>Pagos</strong> (Ventas, Gerente, o administrador — anular un pago es solo para "
      + "administrador/gerente): el panel de un pedido confirmado muestra su historial de liquidación — "
      + "registre pagos parciales (fecha, monto, método, referencia opcional) hasta que el saldo pendiente "
      + "llegue a cero; el sobrepago se rechaza. Un pago erróneo se <strong>anula</strong> (motivo "
      + "requerido) y el saldo pendiente vuelve a crecer. Un pedido con pagos no se puede anular hasta que "
      + "sus pagos se anulen primero. La pantalla de Clientes muestra el saldo pendiente de cada cliente.",

    reportsHeading: "Informes",
    reportsProduction:
      "<strong>Producción</strong> (todos): elija un rango de fechas — huevos por día, pérdidas, "
      + "vendibles, con defecto, muertes, y <strong>% de puesta diaria</strong> (huevos recolectados ÷ "
      + "aves vivas ese día × 100), con totales del período y un desglose por grado. Las entradas en "
      + "borrador y anuladas no cuentan.",
    // #396 — machine-drafted, pending native review (#182).
    reportsCondition:
      "<strong>Con defecto</strong>: huevos rajados y sucios que pasaron a inventario en vez de "
      + "registrarse como pérdida. Se cuentan en <strong>Conteo de huevos</strong>, nunca se clasifican a "
      + "mano, así que no forman parte de <strong>Vendible</strong> — sume ambos para obtener todo lo que "
      + "el día produjo y usted puede vender. El día muestra 0 si esos grados están desactivados en "
      + "Ajustes, y los días registrados antes de activarlos siguen mostrando 0: activar un grado nunca "
      + "reescribe un día pasado.",
    reportsMoney:
      "<strong>Dinero</strong> (admin): resumen de ventas de los pedidos del rango (ingresos / pagado / "
      + "pendiente), gastos por categoría, y <strong>ganancia básica</strong> — ingresos confirmados menos "
      + "gastos registrados, sin costo de bienes vendidos.",
    reportsThrottle:
      "<strong>Si se rechaza un informe</strong>: la granja ejecuta solo unos pocos informes a la vez, para "
      + "que una pantalla ocupada no ralentice la aplicación para los demás. Pedir varios al mismo tiempo — "
      + "varias personas abriendo Informes a la vez, o reintentos repetidos — puede responder "
      + "<strong>inténtelo de nuevo en un momento</strong> en lugar de una tabla. No se registró ni se "
      + "perdió nada: presione <strong>reintentar</strong> en la pantalla de Informes un momento después "
      + "y se vuelve a ejecutar con las mismas fechas que eligió.",

    expensesHeading: "Gastos (admin)",
    expensesRecording:
      "Registre el dinero que sale: fecha, categoría, descripción y monto (en la moneda de la granja), "
      + "opcionalmente vinculado a un lote. El selector de mes muestra un total acumulado; las categorías "
      + "se administran en la misma pantalla (desactivar una la oculta de los gastos nuevos — los ya "
      + "registrados la conservan).",
    expensesCorrections:
      "Las correcciones editan el gasto en su lugar (<strong>corregir</strong> en la fila). Si alguien más "
      + "lo corrigió primero, el formulario recarga sus valores y le pide volver a aplicar. La moneda en "
      + "que se registró un gasto nunca cambia.",
    expensesAdminOnly:
      "Los gastos son datos de dinero, así que toda la pantalla — incluida la visualización — es solo "
      + "para administradores, a diferencia de las pantallas de producción donde los trabajadores registran.",

    historyHeading: "Historial",
    historyBrowse:
      "Explore las entradas diarias registradas de más reciente a más antigua, filtradas por lote y rango "
      + "de fechas. La columna de estado muestra la vida de la entrada: Borrador, Enviada, Bloqueada (7+ "
      + "días), Ajustada (pase el cursor para ver el motivo), o Anulada.",
    historyAdminActions:
      "Los administradores corrigen desde aquí: <strong>ajustar</strong> reabre la entrada en el mismo "
      + "formulario de dos pasos que Entrada diaria — mismo conteo vendible, misma ficha de clasificación, "
      + "mismo atajo <strong>poner todo en…</strong> — con motivo requerido; <strong>anular</strong> "
      + "deshace toda la entrada. Las existencias y el libro mayor de aves se actualizan automáticamente.",
    historyDraftEdit:
      "Las filas en borrador tienen un enlace <strong>editar</strong> (todos, no solo administradores) que "
      + "salta de vuelta a la pantalla de Entrada diaria con ese lote y día cargados — los borradores se "
      + "editan ahí, no se ajustan.",

    auditHeading: "Registro de auditoría (admin)",
    auditLog:
      "Todo cambio correctivo, destructivo o de configuración queda en el registro de auditoría "
      + "automáticamente: quién lo hizo, cuándo (UTC), qué afectó, y el motivo cuando se dio uno. Se "
      + "escribe junto con el cambio mismo — una acción fallida no deja rastro, una exitosa siempre lo hace "
      + "— y nunca es editable, por nadie.",

    exportHeading: "Exportar y respaldo (admin)",
    exportCsv:
      "La pantalla de Exportar descarga sus datos como archivos CSV que puede abrir en cualquier hoja de "
      + "cálculo — un conjunto de datos a la vez, o todo de una vez como un zip (el <strong>respaldo "
      + "completo</strong>, con un manifiesto de conteos de filas). Guarde una copia en un lugar seguro "
      + "según su propio calendario; los respaldos automáticos programados llegan en una fase posterior.",
    exportFormats:
      "Las columnas de dinero contienen unidades menores (centavos) más la moneda — valores exactos, no "
      + "formato de presentación. Las fechas son ISO (AAAA-MM-DD), y las marcas de tiempo son UTC.",

    farmSettingsHeading: "Configuración de la granja (admin)",
    farmSettingsIntro:
      "<strong>Configuración → Configuración de la granja</strong> contiene el nombre de la granja y las "
      + "cuatro cosas que deciden cómo se lee todo: <strong>zona horaria</strong>, <strong>configuración "
      + "regional</strong>, <strong>moneda</strong> y <strong>sistema de unidades</strong>. El primer día de "
      + "la semana y los formatos de fecha y hora son opcionales — déjelos en blanco y la configuración "
      + "regional decide. La zona horaria surte efecto en todas partes en el momento en que se guarda; el "
      + "resto se registra contra la granja e influirá en cómo se muestran los montos, las fechas y las "
      + "medidas una vez que ese formato esté disponible.",
    farmSettingsTimezone:
      "La <strong>zona horaria</strong> es el día de la granja. Cada campo que registra <em>cuándo sucedió "
      + "algo</em> — entrada diaria, lotes, agua, uso y compras de alimento, gastos, pedidos y pagos — se "
      + "abre en ella y se niega a ir más allá, sea cual sea el día en el teléfono o la laptop que tiene en "
      + "la mano, para que un dispositivo adelantado a la granja ya no pueda ofrecer una fecha que el "
      + "guardado luego rechace. Las fechas que están destinadas a estar en el futuro no tienen tope: la "
      + "<strong>caducidad</strong> de un lote de alimento, y los rangos de fechas por los que filtra "
      + "Historial y Agua.",
    farmSettingsCurrency:
      "La <strong>moneda</strong> se bloquea en el momento en que la granja registra su primer monto — una "
      + "venta, un pago, un gasto, un producto con precio, o dinero gastado en alimento. El campo se "
      + "muestra bloqueado con el motivo en lugar de dejarle escribir un código que sería rechazado. Nada "
      + "de lo ya registrado se vuelve a fijar de precio jamás, que es exactamente por qué se bloquea.",
    farmSettingsLogo:
      "El <strong>logotipo</strong> reemplaza la marca de Cluckwork en la barra lateral para todos en la "
      + "granja. PNG, JPEG o WebP, hasta el límite de tamaño mostrado en la pantalla (2 MB por defecto) y "
      + "4096 píxeles por lado. Las imágenes animadas se rechazan en lugar de aplanarse. Lo que se guarda "
      + "es una copia reconstruida: los detalles de cámara y ubicación se eliminan al ingresar — una foto "
      + "tomada en un teléfono lleva dónde se tomó, y para una granja eso es su dirección. Elimínelo y la "
      + "barra lateral vuelve a la marca de Cluckwork.",
    farmSettingsDateTimeFormat:
      "<strong>Formato de fecha</strong> y <strong>formato de hora</strong> ofrecen algunas opciones "
      + "comunes en un menú desplegable — elija una y listo. ¿Necesita algo que no está en la lista? "
      + "Elija <strong>Personalizado…</strong> para escribir el suyo; las mismas reglas que siguen las "
      + "opciones predefinidas siguen aplicando (el servidor lo verifica de todos modos), así que un "
      + "valor inutilizable se rechaza en lugar de guardarse.",
    farmSettingsSquareLogo:
      "Use un logotipo <strong>cuadrado</strong>. Se muestra pequeño en la barra lateral, así que una "
      + "marca simple y bien recortada — un símbolo o una sola letra — se ve mucho mejor ahí que un "
      + "logotipo ancho o una imagen detallada, que se reducen a algo ilegible. Conserve un logotipo "
      + "detallado para impresión o un sitio web; dele a la aplicación una marca pequeña y limpia.",
    farmSettingsCountingUnit:
      "<strong>Unidad de conteo de la entrada diaria</strong> establece cuánto cuentan los botones +/− de "
      + "la pantalla de entrada para todos en la granja — un huevo, o una unidad de empaque como Tray (30 "
      + "por toque). Solo se pueden elegir unidades con una definición activa de huevos por unidad en la "
      + "pantalla de Productos, y cada persona puede anularla para sí misma en su pantalla de Cuenta.",

    farmPaletteHeading: "Paleta de la granja",
    farmPaletteIntro:
      "La configuración de la granja permite a un administrador elegir el color de acento usado en toda "
      + "la aplicación para todos en la granja: Berenjena, Bosque, Pizarra o Terracota. La elección se "
      + "aplica al guardar, y todos la ven la próxima vez que carga su aplicación.",
    farmPaletteLightNight:
      "El modo claro y nocturno son independientes y personales. Cada persona elige el suyo con el "
      + "interruptor en la barra lateral, en cada dispositivo, y la paleta de la granja nunca lo anula — "
      + "cada paleta está diseñada para funcionar en ambos.",

    accountHeading: "Su cuenta",
    accountPassword:
      "<strong>Cambiar contraseña</strong> requiere la actual y cierra la sesión en sus otros dispositivos "
      + "— todos los roles pueden hacerlo por sí mismos.",
    accountLanguage:
      "<strong>Idioma</strong> cambia la interfaz solo para usted, de inmediato, en cada dispositivo donde "
      + "inicie sesión.",
    accountCountingUnit:
      "<strong>Unidad de conteo de la entrada diaria</strong> — cuánto cuentan SUS toques de +/−, "
      + "anulando el valor predeterminado de la granja en Configuración. Elija una unidad de empaque como "
      + "Tray para contar por bandeja, o siga el predeterminado de la granja para que un cambio posterior "
      + "en toda la granja le aplique automáticamente.",

    installHeading: "Instalar en un teléfono",
    installIntro:
      "Cluckwork se puede agregar a la pantalla de inicio de un teléfono o tableta, donde obtiene su "
      + "propio ícono y se abre en su propia ventana sin las barras del navegador — más espacio para las "
      + "pantallas de entrada y más rápido de alcanzar en el galpón. Es la misma aplicación, no una "
      + "descarga separada, así que no hay nada que actualizar desde una tienda de aplicaciones.",
    installSteps:
      "<strong>Android (Chrome):</strong> abra el menú y elija <strong>Instalar aplicación</strong> o "
      + "<strong>Agregar a pantalla de inicio</strong>. <strong>iPhone/iPad (Safari):</strong> toque "
      + "<strong>Compartir</strong>, luego <strong>Agregar a pantalla de inicio</strong>.",
    installHttps:
      "Instalar solo se ofrece a través de una dirección segura (<strong>https</strong>). Si su granja "
      + "accede a Cluckwork en una dirección <strong>http</strong> simple, la opción simplemente no "
      + "aparecerá — nada está roto, y la aplicación funciona exactamente como en el navegador.",
    installOffline:
      "Instalar <strong>no</strong> hace que la aplicación funcione sin conexión. Todavía necesita una "
      + "conexión para cargar y guardar; solo las propias pantallas de la aplicación se guardan en el "
      + "dispositivo para que inicie rápido. Registrar sin conexión es trabajo planeado, no algo que "
      + "instalar active.",
    installNewVersion:
      "Cuando se publica una nueva versión verá <strong>\"Una nueva versión de Cluckwork está "
      + "lista\"</strong>. Espera a que usted esté listo en lugar de recargar mientras está escribiendo — "
      + "presione <strong>Recargar</strong> cuando le convenga, o <strong>Más tarde</strong> y volverá a "
      + "preguntar la próxima vez. No se pierde nada por dejarlo.",

    mistakesHeading: "Corregir errores",
    mistakesIntro:
      "Cada corrección en esta tabla necesita un inicio de sesión de administrador (vea \"Quién puede "
      + "hacer qué\") — los trabajadores registran, los administradores corrigen. La única excepción: un "
      + "<em>borrador</em> todavía se está registrando, no corrigiendo, así que los trabajadores editan sus "
      + "propios borradores.",
    mistakesTableMistakeHeader: "Error",
    mistakesTableFixHeader: "Corrección",

    mistakesRow1Mistake: "Agotó o archivó el lote equivocado",
    mistakesRow1Fix: "Lotes → <strong>Reactivar</strong> (totalmente reversible)",

    mistakesRow2Mistake: "Conteo de aves incorrecto",
    mistakesRow2Fix: "Lotes → libro mayor de aves → <strong>Ajuste</strong> (en cualquier dirección)",

    mistakesRow3Mistake: "Confirmó el pedido de venta equivocado",
    mistakesRow3Fix:
      "Ventas → abra el pedido → <strong>Anular pedido</strong> (las existencias regresan a sus lotes; "
      + "motivo requerido). Si se registraron pagos en él, anúlelos primero.",

    mistakesRow4Mistake: "Registró un pago incorrecto",
    mistakesRow4Fix:
      "Ventas → abra el pedido → pagos → <strong>anular</strong> (motivo requerido): la fila se conserva y "
      + "el saldo pendiente vuelve a crecer.",

    mistakesRow5Mistake: "<em>Cantidad</em> incorrecta en una compra de alimento / alimento echado a perder",
    mistakesRow5Fix:
      "Inventario → abra el artículo → <strong>Corregir existencias</strong> (Ajuste o Descarte contra el "
      + "lote; motivo requerido). Solo las cantidades son corregibles — un costo, fecha o número de lote "
      + "incorrecto aún no se puede corregir, así que verifique eso antes de guardar.",

    mistakesRow6Mistake: "Uso de alimento sobre o subregistrado",
    mistakesRow6Fix:
      "Mismo formulario de corrección: un Ajuste positivo devuelve al lote las existencias usadas de más "
      + "(hasta lo que recibió); uno negativo elimina existencias subregistradas. El registro de uso en sí "
      + "y su estimación de costo permanecen como se registraron — las correcciones arreglan las "
      + "existencias, no el historial.",

    mistakesRow7Mistake: "Registro de agua incorrecto",
    mistakesRow7Fix:
      "Agua → <strong>corregir</strong> en el registro — cantidades, fuente, medidores y nota se editan en "
      + "su lugar (sin existencias detrás del agua). El lote y la fecha son fijos: si se eligió mal, "
      + "regístrelo de nuevo bajo el correcto.",

    mistakesRow8Mistake: "Números incorrectos en una entrada diaria <em>enviada</em>",
    mistakesRow8Fix:
      "Historial → <strong>ajustar</strong> (admin) — totales, pérdidas, mortalidad y desglose por grado, "
      + "con un motivo requerido. Los grados corregidos deben sumar exactamente la cantidad vendible "
      + "corregida, la misma regla que usa Enviar, y <strong>Guardar ajuste</strong> se bloquea hasta que "
      + "coincidan. Las existencias y el libro mayor de aves se actualizan para coincidir automáticamente, "
      + "pero los huevos ya vendidos nunca se pueden descontar: reducir un grado por debajo de lo vendido se "
      + "rechaza. Los valores anteriores permanecen visibles en la entrada.",

    mistakesRow9Mistake: "Toda la entrada <em>enviada</em> está equivocada (lote o día incorrecto)",
    mistakesRow9Fix:
      "Historial → <strong>anular</strong> (admin, motivo requerido): sus lotes de huevos se vacían, sus "
      + "muertes se revierten en el libro mayor de aves, y la entrada se conserva como Anulada. Se rechaza "
      + "si alguno de sus huevos ya se vendió — anule la venta primero. Anular libera el día: la entrada "
      + "correcta puede entonces registrarse para el mismo lote y fecha.",

    mistakesRow10Mistake: "Error en una entrada u orden en <em>borrador</em>",
    mistakesRow10Fix:
      "Edítela — los números del borrador, las líneas de grado y las líneas de pedido son todas editables "
      + "(entradas en borrador: Historial → <strong>editar</strong> salta a la pantalla de Entrada diaria "
      + "con el día cargado). El lote/fecha de una entrada y el cliente/fecha de un pedido son fijos, sin "
      + "embargo: si se eligió mal, simplemente regístrelo de nuevo bajo el correcto (y cancele el pedido "
      + "en borrador equivocado).",

    // machine-drafted (#182) — pending native review. Task 33 (B6b): the
    // glossary table (37 rows) + closing repo-note, formal "usted" register
    // matching the rest of this pack. Keys and tags mirror en.ts exactly —
    // catalogParity enforces key-set and tag parity. The
    // "specs/product/GLOSSARY.md" path inside glossaryRepoNote's <code> tag
    // is kept literal (untranslated), per the Task 33 brief.
    glossaryHeading: "Glosario",

    glossaryNavigationTerm: "Navegación",
    glossaryNavigationDef:
      "En una computadora, las pantallas están en la barra lateral izquierda; en un teléfono, las cuatro "
      + "que más usa aparecen como pestañas en la parte inferior, y el resto está bajo Más.",

    glossaryOperationalDayTerm: "Día operativo",
    glossaryOperationalDayDef:
      "Las fechas significan el día calendario de su granja, calculado según la zona horaria propia de la "
      + "granja y no la de un reloj en otro lugar. Es el mismo \"hoy\" en todas partes: lo que cuenta como "
      + "fecha futura al registrar trabajo, cuándo salen los huevos de un período de retiro, qué huevos "
      + "puede tomar una venta, el día en que se agota o archiva un lote, y el rango en que se abren los "
      + "informes. Todo campo que registra CUÁNDO OCURRIÓ ALGO se abre en ese día y no permite ir más "
      + "allá, sin importar en qué día esté el dispositivo que tiene en la mano. Las fechas que deben caer "
      + "en el futuro no tienen ese límite — el vencimiento de un lote de alimento, y los filtros de "
      + "Historial y Agua.",

    glossaryInstallToHomeScreenTerm: "Instalar en la pantalla de inicio",
    glossaryInstallToHomeScreenDef:
      "Agregar Cluckwork a la pantalla de inicio de un teléfono o tableta desde el navegador, para que "
      + "tenga su propio ícono y se abra en su propia ventana sin las barras del navegador. Es la misma "
      + "aplicación, no una descarga aparte — nada que actualizar desde una tienda de aplicaciones. Solo se "
      + "ofrece por una dirección segura (https), y <strong>no</strong> hace que la aplicación funcione sin "
      + "conexión: todavía necesita conexión para cargar y guardar.",

    glossaryNewVersionReadyTerm: "Hay una nueva versión disponible",
    glossaryNewVersionReadyDef:
      "Después de un lanzamiento, una aplicación instalada detecta la nueva versión en segundo plano y "
      + "pregunta antes de cambiar, en lugar de recargar mientras está escribiendo. Presione Recargar "
      + "cuando le convenga, o Más tarde y volverá a preguntar la próxima vez. No se pierde nada por "
      + "dejarlo — la aplicación en ejecución sigue funcionando hasta que acepte.",

    glossaryTooManyReportsTerm: "Demasiados informes a la vez",
    glossaryTooManyReportsDef:
      "La granja ejecuta solo unos pocos informes al mismo tiempo, para que una pantalla ocupada no "
      + "ralentice la aplicación para todos. Por encima de eso, un informe responde pidiendo que lo "
      + "intente de nuevo en un momento en lugar de esperar en cola. No se registra ni se pierde nada — "
      + "presione reintentar en la pantalla de Informes un momento después; se vuelve a ejecutar con las "
      + "mismas fechas que eligió. Cada granja tiene su propio margen, así que los informes de otra granja "
      + "nunca consumen el suyo.",

    glossaryTooManySignInAttemptsTerm: "Demasiados intentos de inicio de sesión",
    glossaryTooManySignInAttemptsDef:
      "El inicio de sesión tiene un límite de frecuencia para frenar los intentos de adivinar contraseñas: "
      + "demasiados intentos desde un mismo lugar en pocos minutos se rechazan con este mensaje hasta que "
      + "pasa un breve período de espera. Nunca afecta a una sesión que ya inició.",

    // #393 (machine-drafted, pending native review)
    glossaryForcedReauthTerm: "Sesión cerrada justo después de cambiar de cuenta",
    glossaryForcedReauthDef:
      "Iniciar sesión como otra persona en una <strong>pestaña del navegador</strong> mientras otra pestaña "
      + "está a mitad de su propia comprobación silenciosa en segundo plano puede, en ocasiones, cerrar la "
      + "nueva sesión de inmediato. Simplemente vuelva a iniciar sesión — esto solo ocurre en ese momento "
      + "puntual de varias pestañas y no se pierde nada de lo ya guardado.",

    // #308 (machine-drafted, pending native review)
    glossaryStepUpAuthTerm: "Autenticación reforzada (step-up)",
    glossaryStepUpAuthDef:
      "Una comprobación adicional además de haber iniciado sesión: antes de crear otro propietario o "
      + "restablecer la contraseña de un propietario existente, la pantalla Usuarios le pide volver a "
      + "ingresar su contraseña actual directamente en el diálogo. Esto confirma que realmente es usted "
      + "antes de otorgar tanto acceso — ninguna otra acción en esa pantalla vuelve a preguntar.",

    glossarySomethingWentWrongScreenTerm: "Pantalla \"Algo salió mal\"",
    glossarySomethingWentWrongScreenDef:
      "Lo que muestra una pantalla cuando encuentra un error, en lugar de quedar en blanco. Los datos "
      + "guardados están seguros — es posible que deba volver a escribir lo que aún estaba ingresando; "
      + "toque Recargar o Volver al panel. \"Detalles del error\" contiene el mensaje para una captura de "
      + "pantalla.",

    glossaryDailyEntryTerm: "Entrada diaria",
    glossaryDailyEntryDef: "El día de un lote: huevos por grado, pérdidas, muertes. Clasificar por encima del total lo eleva para igualarlo. Borrador hasta que se envía.",

    glossaryEggLotTerm: "Lote de huevos",
    glossaryEggLotDef:
      "Un lote fechado de huevos vendibles de un grado, creado al enviar una entrada. Las existencias son "
      + "la suma de los lotes.",

    glossaryGradeTerm: "Grado",
    glossaryGradeDef:
      "Una categoría de clasificación (tamaño, calidad o personalizada). Los grados vendibles se pueden "
      + "vender.",

    glossaryEggMovementLedgerTerm: "Libro mayor de movimientos de huevos",
    glossaryEggMovementLedgerDef:
      "El historial línea por línea detrás del saldo de un lote de huevos: producción que entra, ventas "
      + "que salen, correcciones y anulaciones con su signo correspondiente.",

    glossaryFifoTerm: "FIFO",
    glossaryFifoDef:
      "\"Primero en entrar, primero en salir\" — las ventas y el uso de alimento siempre toman primero las "
      + "existencias más antiguas.",

    glossaryCullTerm: "Descarte",
    glossaryCullDef:
      "Aves retiradas deliberadamente de un lote (vendidas, sacrificadas, regaladas) — no son muertes.",

    glossaryMortalityTerm: "Mortalidad",
    glossaryMortalityDef:
      "Muertes, registradas en la entrada diaria; llegan al libro mayor de aves automáticamente al enviar.",

    glossaryDepleteTerm: "Agotar",
    glossaryDepleteDef:
      "Marcar un lote como sin aves restantes. El historial se conserva; reversible mediante Reactivar.",

    glossaryArchiveTerm: "Archivar",
    glossaryArchiveDef: "Ocultar un lote terminado del trabajo diario. Reversible mediante Reactivar.",

    glossaryWithdrawalRestrictionTerm: "Restricción de retiro",
    glossaryWithdrawalRestrictionDef:
      "Una retención sobre los huevos durante un período de retiro por medicación. Llegará con el "
      + "seguimiento de medicación — todavía nada establece restricciones, así que por ahora gestione los "
      + "períodos de retiro fuera de Cluckwork.",

    glossaryProductTerm: "Producto",
    glossaryProductDef:
      "Lo que usted vende — un producto de huevo apunta a un grado (su fuente de existencias) y lleva una "
      + "unidad de venta y un precio predeterminado.",

    glossaryPackedUnitTerm: "Unidad empacada",
    glossaryPackedUnitDef:
      "Cuántos huevos contiene una docena/bandeja/cartón/caja en su granja. Cada línea de venta conserva "
      + "el conteo con el que se vendió.",
    glossaryCountingUnitTerm: "Unidad de conteo",
    glossaryCountingUnitDef:
      "Cuánto cuenta cada toque de los botones − / + de la entrada diaria — un huevo, o una unidad "
      + "empacada como una bandeja. Predeterminado de la granja en Configuración; su propia elección en su "
      + "pantalla de Cuenta. Los botones muestran la cantidad (−30 / +30) cuando no es uno.",

    glossarySalesLineTerm: "Línea de venta",
    glossarySalesLineDef:
      "Un producto en un pedido: una cantidad entera en unidades de venta, con precio por unidad (el "
      + "precio puede tener decimales); los huevos detrás de ella son la cantidad × el conteo de huevos de "
      + "la unidad.",

    glossaryConfirmOrderTerm: "Confirmar (pedido)",
    glossaryConfirmOrderDef:
      "Convierte un pedido en borrador en una venta real y asigna existencias. Solo se deshace anulando.",

    glossaryVoidOrderTerm: "Anular (pedido)",
    glossaryVoidOrderDef:
      "Deshacer una confirmación equivocada — las existencias regresan exactamente a los lotes de donde "
      + "vinieron. Requiere un motivo.",

    glossaryCancelOrderTerm: "Cancelar (pedido)",
    glossaryCancelOrderDef: "Cerrar un borrador que nunca se concretó. No involucra existencias.",

    glossaryInventoryItemTerm: "Artículo de inventario",
    glossaryInventoryItemDef:
      "Una entrada del catálogo para algo que usted almacena (alimento, suplementos…), con una unidad de "
      + "medida fija.",

    glossaryInventoryLotTerm: "Lote de inventario",
    glossaryInventoryLotDef:
      "Un lote recibido de un artículo, con su propio costo. Existencia disponible = suma de los lotes.",

    glossaryInventoryMovementLedgerTerm: "Libro mayor de movimientos de inventario",
    glossaryInventoryMovementLedgerDef:
      "El rastro de solo adición de cada cambio en las existencias de alimento/suministros. Las "
      + "correcciones son filas nuevas, nunca ediciones.",

    glossaryWaterUsageTerm: "Uso de agua",
    glossaryWaterUsageDef:
      "Lo que bebió un lote en un día — cantidad directa o delta de medidor. Editable en el lugar; "
      + "lote/fecha fijos.",

    glossaryFeedUsageTerm: "Uso de alimento",
    glossaryFeedUsageDef:
      "Lo que comió un lote en un día; consume lotes en orden FIFO y estima el costo a partir de ellos.",

    glossaryAdjustmentDiscardTerm: "Ajuste / Descarte",
    glossaryAdjustmentDiscardDef:
      "Correcciones de existencias contra un lote, motivo requerido. Descarte = baja (deterioro).",

    glossaryRolesTerm: "Roles",
    glossaryRolesDef:
      "Admin (propietario), Gerente, Trabajador, Ventas, Solo lectura — vea \"Quién puede hacer qué\". Los "
      + "trabajadores registran; los gerentes también corrigen y configuran; ventas gestiona pedidos y "
      + "pagos; solo lectura únicamente visualiza.",

    glossaryLockedEntryTerm: "Bloqueada (entrada)",
    glossaryLockedEntryDef:
      "Una entrada enviada de más de 7 días — cerrada a ediciones rutinarias; el ajuste/anulación de admin "
      + "sigue funcionando.",

    glossaryAdjustEntryTerm: "Ajustar (entrada)",
    glossaryAdjustEntryDef:
      "Corrección de admin sobre una entrada enviada. Los grados corregidos deben sumar exactamente la "
      + "cantidad vendible corregida, la misma regla que usa Enviar — un ajuste no tiene estado de borrador "
      + "para dejarlo parcialmente clasificado. Las existencias y el libro mayor de aves se concilian "
      + "automáticamente; los huevos vendidos son intocables; los valores anteriores permanecen "
      + "visibles.",

    glossaryVoidEntryTerm: "Anular (entrada)",
    glossaryVoidEntryDef:
      "Deshacer de admin de toda una entrada enviada — los lotes quedan vacíos, las muertes se revierten, "
      + "la entrada se conserva como Anulada. Se rechaza una vez que sus huevos están vendidos.",

    glossaryFarmSettingsTerm: "Configuración de la granja",
    glossaryFarmSettingsDef:
      "El nombre de la granja, la zona horaria, la configuración regional, la moneda y el sistema de "
      + "unidades, además del primer día de la semana y los formatos de fecha/hora, opcionales — elegidos "
      + "de un menú de opciones predefinidas, o escritos como una cadena de formato .NET personalizada. "
      + "Configuración → Configuración de la granja; propietarios y gerentes editan, todos pueden leer — "
      + "dar formato a montos y fechas no es un permiso.",

    glossaryCurrencyLockTerm: "Bloqueo de moneda",
    glossaryCurrencyLockDef:
      "La moneda de la granja deja de ser editable en cuanto algo registra un monto en ella — una venta, "
      + "un pago, un gasto, un producto con precio, dinero gastado en alimento. El campo se muestra "
      + "bloqueado con el motivo. Nada de lo ya registrado se vuelve a valorar jamás, que es todo el punto.",

    glossaryFarmLogoTerm: "Logotipo de la granja",
    glossaryFarmLogoDef:
      "Su propia imagen en lugar de la marca de Cluckwork en la barra lateral, subida desde Configuración "
      + "de la granja. PNG, JPEG o WebP (2 MB por defecto), solo imágenes fijas; una marca cuadrada y "
      + "simple se lee mejor en el tamaño pequeño de la barra lateral. Se guarda como una copia "
      + "reconstruida sin los detalles de cámara y ubicación.",

    glossaryFarmPaletteTerm: "Paleta de la granja",
    glossaryFarmPaletteDef:
      "El color de acento de toda la granja, elegido por un admin en Configuración de la granja. "
      + "Independiente de la configuración de modo claro/nocturno de cada persona.",

    glossaryUiLanguageTerm: "Idioma de la interfaz",
    glossaryUiLanguageDef:
      "El idioma por usuario en que se muestra la interfaz — inglés, español o tagalo — elegido desde "
      + "Cuenta → Preferencias. El inglés es el idioma de reserva para cualquier pantalla aún no "
      + "traducida, sea cual sea el idioma que eligió.",

    glossaryRepoNote:
      "Las definiciones completas en lenguaje de especificación viven en "
      + "<code>specs/product/GLOSSARY.md</code> del repositorio.",
  },
} as const;
