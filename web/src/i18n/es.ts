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
    tooManyAttempts:
      "Demasiados intentos de inicio de sesión. Espere unos minutos y vuelva a intentarlo.",
    apiDown: "No se pudo iniciar sesión. ¿Está la API en ejecución?",
  },
  account: {
    preferences: "Preferencias",
    language: "Idioma",
    languageHint: "El idioma en que se muestra la interfaz, solo para usted.",

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

    // Status-filter options
    allOption: "Todos",
    statusDraft: "Borrador",
    statusConfirmed: "Confirmado",
    statusCancelled: "Cancelado",
    statusVoided: "Anulado",

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
    usageRecordedMessage:
      "Uso de alimento registrado — se consumieron primero las existencias "
      + "de los lotes más antiguos.",
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
    recordUsageButton: "Registrar uso",
    correctStockButton: "Corregir existencias",
    notFeedableMessage:
      "Los artículos de {{category}} no se dan de comer a los lotes — el "
      + "uso solo aplica a artículos de Alimento, Suplemento y Aditivo.",
    noFlocksForUsageMessage: "No hay lotes — el uso necesita un lote para alimentar.",
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
    recordUsageDialogTitle: "Registrar uso — {{name}}",
    flockLabel: "Lote",
    depletedFlockSuffix: " (agotado — solo para registrar fechas pasadas)",
    dateLabel: "Fecha",
    recordUsageSubmitButton: "Registrar uso",

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
    exceedsSellableMessage:
      "Las cantidades clasificadas no pueden superar el total de huevos menos los agrietados/sucios/descartados.",
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
    totalEggsLabel: "Total de huevos",
    crackedLabel: "Agrietados",
    dirtyLabel: "Sucios",
    discardedLabel: "Descartados",
    deathsLabel: "Muertes",
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
    "auditAction.User.FlockAssign": "Lote asignado al usuario",
    "auditAction.User.FlockUnassign": "Lote desasignado del usuario",
    "auditAction.Account.Export": "Datos exportados",
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
    "entityType.Flock": "Lote",
    "entityType.InventoryItem": "Artículo de inventario",
    "entityType.Payment": "Pago",
    "entityType.Product": "Producto",
    "entityType.SalesOrder": "Pedido de venta",
    "entityType.User": "Usuario",
    "entityType.WaterUsage": "Uso de agua",
  },
} as const;
