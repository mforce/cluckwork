# i18n Conventions

The Cluckwork SPA is built for internationalization from the start. This guide documents the patterns used for translatable strings, keying, and formatting boundaries.

## Choosing the right API: `t()` vs. `<Trans>`

**Use `t()` for plain strings** — the most common case:

```tsx
const { t } = useTranslation("auth");

return <button>{t("signIn")}</button>;
```

**Use `<Trans>` only for strings that interleave JSX elements** (like bold, links, or icons):

```tsx
<Trans
  ns="sales"
  i18nKey="paymentsSummary"
  values={{
    paid: formatMoney(…),
    outstanding: formatMoney(…),
  }}
  components={{ strong: <strong /> }}
/>
```

### Important: React-i18next 17 typing quirk

When using `<Trans>`, the key must be:
- Prefixed with the namespace **in the `ns` prop**, not in the key
- Use an **unprefixed key** in `i18nKey` (NOT `i18nKey="ns:key"`)

```tsx
// ✓ Correct — ns="sales" + unprefixed i18nKey
<Trans ns="sales" i18nKey="paymentsSummary" …/>

// ✗ Wrong — will not typecheck
<Trans i18nKey="sales:paymentsSummary" …/>
```

## Key naming

Keys follow the pattern **`namespace:camelCaseKey`**.

Namespaces are by area:
- `common` — universal actions (save, cancel, retry)
- `auth` — login & authentication
- `account` — user preferences
- `sales` — the sales & orders module
- `errors` — API validation messages keyed by [stable error codes](./src/i18n/en.ts#L27) (`Me.Language.Format`)

Examples:
- `auth:signIn` — a button label
- `sales:confirmOrderTitle` — a dialog title
- `errors:Me.Language.Format` — a form error message

## Catalog and type safety

The **source of truth and fallback** is [`src/i18n/en.ts`](./src/i18n/en.ts). All strings are English, sentence-case UI copy. When you add a key to the catalog, it automatically extends the compile-time type for `t()` via [`src/types/i18next.d.ts`](./src/types/i18next.d.ts). A typo in a key becomes a **build error**, not a silent runtime miss:

```tsx
const { t } = useTranslation("auth");

t("signIn");   // ✓ Compiles — key exists in en.ts
t("siginIn");  // ✗ Build error — typo caught at compile time
```

## Interpolation

To substitute variables, pass an object to `t()` and use `{{var}}` placeholders in the catalog:

**Catalog** (`src/i18n/en.ts`):
```typescript
sales: {
  orderTotal: "Total: {{amount}}",
  atMostDecimals: "At most {{count}} decimal places for this currency.",
}
```

**Component**:
```tsx
const { t } = useTranslation("sales");

t("orderTotal", { amount: "$10.50" })
t("atMostDecimals", { count: 2 })
```

## Strings outside render: the imperative pattern

Strings built in event handlers, module-level helpers, or any non-render code cannot call the `useTranslation` hook (it's a Hook and must follow Hook rules). Use the **imperative singleton** instead:

```tsx
import i18n from "../i18n";

// ✓ Module-level helper — inside the hook's render context
function messageFor(err: unknown): string {
  if (err instanceof ApiError && err.status === 401) {
    return i18n.t("auth:invalidCredentials");
  }
  return i18n.t("auth:apiDown");
}

// ✓ Event handler — called during render phase
async function onSubmit(e: FormEvent) {
  try {
    await login(email, password);
  } catch (err) {
    setError(messageFor(err));  // Uses imperative i18n.t()
  }
}
```

See [`Login.tsx`](./src/routes/Login.tsx) for a worked example.

## New screens must add keys

A hardcoded user-facing string is a review defect. Every new screen must:

1. Create a namespace in [`src/i18n/en.ts`](./src/i18n/en.ts) if it doesn't exist
2. Add keys for all UI text (labels, buttons, messages, placeholders)
3. Use `t()` or `<Trans>` to render each key
4. Include tests (see [the coverage gate in `web/README.md`](./README.md#tests))

Screens that went through the pilot (Login, SalesPage) are worked examples of the full string sweep. The remaining screens are tracked in #182.

## Formatting boundary: money, dates, and numbers

**Critical rule:** Money, dates, and numbers must **never** key off the UI language. They are driven by the farm's locale — its timezone, currency, and locale code — which a user cannot change from the UI language picker.

```tsx
// ✓ Correct — money is formatted by farm currency + minor units,
//   regardless of UI language.
formatMoney(1050, "USD", 2)  // → "10.50 USD"

// ✓ Correct — date formatted by farm timezone, not UI language.
todayIso(farm.timezone)  // → "2026-07-26"
```

This is enforced by a [guard test](./src/i18n/formattingIndependence.test.ts) — changing the UI language does not change money or date output. If you're tempted to key a price or date off `i18n.language`, you've found the formatting boundary.

## Adding a language pack

To ship a new UI language:

1. **Create the pack** — `src/i18n/<code>.ts`, mirroring [`en.ts`](./src/i18n/en.ts)'s structure and keys **exactly**: same namespaces, same keys, in the same order is nice-to-have but not required. Keep every `{{placeholder}}` and every `<Trans>` tag structure (e.g. `<strong>`) **verbatim** — only the translated text changes. Export the catalog under the language code (e.g. `export const fr = { … } as const;`).
2. **Register it** in [`src/i18n/index.ts`](./src/i18n/index.ts): import the pack, add it to the `resources` object (`resources: { en, es, tl, <code> }`), and add its code to `SUPPORTED_LANGUAGES`.
3. **Name it** — add its display name, written in its own script (not translated), to `LANGUAGE_NAMES` in [`src/session/LanguageSelector.tsx`](./src/session/LanguageSelector.tsx).
4. **Mark provenance** — if the pack wasn't reviewed by a native speaker, say so in a header comment, following the convention at the top of [`es.ts`](./src/i18n/es.ts) / [`tl.ts`](./src/i18n/tl.ts) (`MACHINE-DRAFTED translation, PENDING NATIVE-SPEAKER REVIEW`).

**Parity is enforced, not just conventional.** [`src/i18n/catalogParity.test.ts`](./src/i18n/catalogParity.test.ts) fails the build if a pack is missing any of `en`'s keys, carries extra ones, or has an empty value anywhere. This matters because a missing key doesn't error at runtime — with `fallbackLng: "en"` it silently renders the English string instead, which is easy to miss in review without the test catching it.

The current `es` and `tl` packs are **machine-drafted, pending native-speaker review**, and only cover the screens already externalized to the catalog (login, sales, account, errors) — the rest of the app still renders in English until the full string sweep (#182) completes. A newly added pack inherits the same limitation until its screens are externalized too: the parity test only checks the packs against each other, not the app against a hardcoded-string scan, so shipping a pack does not by itself translate a screen that still has hardcoded English strings.

## Worked examples

- **Login screen** ([`src/routes/Login.tsx`](./src/routes/Login.tsx)): simple keys, module-level helper with imperative `i18n.t()`, no interpolation
- **Sales page** ([`src/routes/SalesPage.tsx`](./src/routes/SalesPage.tsx)): namespaced keys, interpolation, `<Trans>` with JSX, farm-locale formatting

See also [`src/i18n/en.ts`](./src/i18n/en.ts) — the full catalog and comment notes.
