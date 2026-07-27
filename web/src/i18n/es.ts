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
} as const;
