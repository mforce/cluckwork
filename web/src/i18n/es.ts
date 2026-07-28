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
  },
} as const;
