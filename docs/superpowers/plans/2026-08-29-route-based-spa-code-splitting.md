# Route-Based SPA Code Splitting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Load each authenticated SPA screen from its own Vite chunk while keeping the authenticated shell visible and every emitted chunk available to the offline-first PWA.

**Architecture:** Keep `Login`, `ProtectedRoute`, `SessionProvider`, `AppLayout`, and the forced-password path in the eager entry graph. Declare the 20 authenticated content components with ordinary `React.lazy` named-export adapters in `App.tsx`, and put one content-only `Suspense` boundary at the existing `AppLayout` outlet seam. Extend the existing generated-service-worker verifier instead of adding a chunk manifest or loader abstraction.

**Tech Stack:** React 19, React Router, TypeScript, Vite, Vitest/Testing Library, vite-plugin-pwa/Workbox, Playwright.

## Global Constraints

- The approved implementation scope is exactly eleven files: `web/src/App.tsx`, `web/src/routes/AppLayout.tsx`, `web/src/routes/AppLayout.test.tsx`, `web/scripts/verify-sw.mjs`, `web/src/routes/HelpPage.tsx`, `web/src/routes/HelpPage.test.tsx`, `web/src/i18n/en.ts`, `web/src/i18n/es.ts`, `web/src/i18n/tl.ts`, `specs/product/GLOSSARY.md`, and `tools/simulation/ui/specs/pwa.spec.ts`. The last file is an owner-approved CI synchronization correction: wait for `navigator.serviceWorker.ready` before reloading in each existing PWA test; do not add retries, increase timeouts, add a helper, or change product code.
- Do not add a server/runtime flag, runtime-config channel, second eager route graph, route registry, generic lazy-loader helper, default-export rewrite, `manualChunks`, dependency, CSS, backend change, or locale-catalog splitting (#597).
- `Login`, `ProtectedRoute`, `SessionProvider`, `AppLayout`, and the conditional `SetPasswordPage` remain eager.
- Preserve all 20 existing authenticated paths and their component identity exactly.
- Reuse `common:loading`; do not hardcode the loading status or add another loading key.
- The existing screen `ErrorBoundary` stays around the routed content; the sidebar, bottom navigation, farm warning, and app shell stay outside the pending state.
- Every emitted `dist/assets/*.js` file must appear by exact normalized URL inside the array passed to generated `precacheAndRoute`.
- Keep all en/es/tl Help key sets in parity and update both in-app and product glossaries.
- Warnings are errors for .NET, but this slice is frontend-only. Do not touch .NET files or run migrations.
- Baseline at `0955095f3185471a55a6890adfa827bb29dd518e`: 87 Vitest files / 1,999 tests pass; the production build emits one `886.22 kB` (`253.98 kB` gzip) JS entry and Vite's >500 kB warning.
- If route splitting does not remove the route-weight >500 kB warning, stop and report the measured chunk composition. Do not raise `chunkSizeWarningLimit` or introduce `manualChunks`.
- Do not commit on `main`. Commit/push/PR steps run only after the owner explicitly authorizes them and the driver creates a `feat/...` branch before dispatch; otherwise the implementer leaves a bounded working-tree diff and reports it.

---

### Task 1: Keep the shell mounted through pending and rejected route modules

**Files:**
- Modify: `web/src/routes/AppLayout.test.tsx:1-302`
- Modify: `web/src/routes/AppLayout.tsx:1-155`

**Interfaces:**
- Consumes: the existing `AppLayout` outlet seam, `ErrorBoundary(scope="screen")`, `common:loading`, and the real links produced by `navGroups`.
- Produces: one `Suspense` boundary whose fallback is `<p className="muted" role="status">…</p>` and whose only child is `<Outlet />`.

- [ ] **Step 1: Add a failing real-navigation pending-route test**

Extend the test imports:

```tsx
import { lazy, type ReactElement } from "react";
import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { act, render, screen, fireEvent, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router";
```

Add this describe block. It starts on a resolved dashboard, clicks the real sidebar Stock link, proves the previous content is replaced, proves the shell remains, and overrides the catalog so a hardcoded English fallback cannot pass:

```tsx
describe("AppLayout lazy route containment (#595)", () => {
  it("shows catalog loading status only in the content pane while shell navigation remains", async () => {
    let resolveScreen!: (value: { default: () => ReactElement }) => void;
    const DeferredScreen = lazy(() => new Promise<{ default: () => ReactElement }>((resolve) => {
      resolveScreen = resolve;
    }));
    const original = i18n.getResource("en", "common", "loading") as string;
    i18n.addResource("en", "common", "loading", "ROUTE-LOADING-MARKER");

    try {
      renderWithProviders(
        <Routes>
          <Route element={<AppLayout />}>
            <Route index element={<p>Resolved dashboard</p>} />
            <Route path="stock" element={<DeferredScreen />} />
          </Route>
        </Routes>,
        { token: { sub: "u1", role: "Admin" } },
      );

      expect(screen.getByText("Resolved dashboard")).toBeInTheDocument();
      fireEvent.click(sidebar().getByRole("link", { name: "Stock" }));

      expect(await screen.findByRole("status")).toHaveTextContent("ROUTE-LOADING-MARKER");
      expect(screen.queryByText("Resolved dashboard")).not.toBeInTheDocument();
      expect(sidebar().getByRole("link", { name: "Dashboard" })).toBeInTheDocument();

      await act(async () => {
        resolveScreen({ default: () => <p>Deferred stock</p> });
      });
      expect(await screen.findByText("Deferred stock")).toBeInTheDocument();
      expect(screen.queryByRole("status")).not.toBeInTheDocument();
    } finally {
      i18n.addResource("en", "common", "loading", original);
    }
  });
```

- [ ] **Step 2: Add a rejected-module containment test**

Complete the same describe block with a rejected `React.lazy` loader. Suppress only React's expected error log and restore it in `finally`:

```tsx
  it("contains a rejected route module in the screen boundary while the shell survives", async () => {
    const RejectedScreen = lazy(() => Promise.reject(new Error("ROUTE-CHUNK-REJECTION")));
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => undefined);

    try {
      renderWithProviders(
        <Routes>
          <Route element={<AppLayout />}>
            <Route index element={<p>Resolved dashboard</p>} />
            <Route path="stock" element={<RejectedScreen />} />
          </Route>
        </Routes>,
        { token: { sub: "u1", role: "Admin" } },
      );

      fireEvent.click(sidebar().getByRole("link", { name: "Stock" }));

      const fallback = await screen.findByRole("alert");
      expect(fallback).toHaveTextContent("Something went wrong");
      expect(fallback).toHaveTextContent("ROUTE-CHUNK-REJECTION");
      expect(sidebar().getByRole("link", { name: "Dashboard" })).toBeInTheDocument();
    } finally {
      consoleError.mockRestore();
    }
  });
});
```

- [ ] **Step 3: Run the targeted test and confirm the pending case is red**

Run:

```bash
cd web
npm test -- --run src/routes/AppLayout.test.tsx
```

Expected: the pending-navigation test fails because no `role="status"` containing `ROUTE-LOADING-MARKER` exists. The rejection case may already reach the existing boundary; record its result separately rather than requiring both tests to fail.

- [ ] **Step 4: Add the minimal content-only Suspense boundary**

Change the React import and add the common translator:

```tsx
import { Suspense, useEffect } from "react";

export function AppLayout() {
  const { t } = useTranslation("nav");
  const { t: tc } = useTranslation("common");
  const { logout, isAdmin, role } = useAuth();
  const { farm, loadFailed, refresh } = useFarm();
  const navigate = useNavigate();
  const location = useLocation();
```

Replace only the outlet inside the existing screen boundary:

```tsx
<ErrorBoundary key={location.key} scope="screen">
  <Suspense fallback={<p className="muted" role="status">{tc("loading")}</p>}>
    <Outlet />
  </Suspense>
</ErrorBoundary>
```

Do not wrap `<main>`, `.shell`, either navigation, `FarmBrand`, or `BottomNav` in `Suspense`.

- [ ] **Step 5: Run the targeted test green and execute the two boundary mutations**

Run the same targeted command and expect every `AppLayout` test to pass. Then, one mutation at a time:

1. Move `Suspense` outside the `.shell` root so navigation is inside the pending subtree; the named pending-navigation test must fail. Under concurrent navigation the first failure may be the missing status (React retains the revealed shell) or the missing sidebar (the outer fallback replaces it), so record the first failing assertion without requiring one scheduler outcome.
2. Remove the screen `ErrorBoundary`; the rejected-module test must fail because the screen fallback and shell are no longer both present.

Restore each mutation with `apply_patch` and rerun the targeted file green.

- [ ] **Step 6: Checkpoint the shell increment**

```bash
git diff --check -- web/src/routes/AppLayout.tsx web/src/routes/AppLayout.test.tsx
git status --short -- web/src/routes/AppLayout.tsx web/src/routes/AppLayout.test.tsx
```

If and only if the owner has explicitly authorized commits and the current branch is not `main`, commit these two files as `feat(web): contain lazy route loading in app shell`. Otherwise do not stage or commit them.

---

### Task 2: Split every authenticated content route without changing route identity

**Files:**
- Modify: `web/src/App.tsx:1-83`
- Verification-only, do not commit: `/home/mforce/.agents/feature-driver-artifacts/cluckwork-595/verify-route-chunks.mjs`

**Interfaces:**
- Consumes: the exact 20 path/component pairs already registered in `App.tsx` and each route module's existing named export.
- Produces: 20 direct `React.lazy` bindings; the route table itself remains byte-for-byte equivalent in paths and component names.

- [ ] **Step 1: Prove the untouched eager graph is red under the exact route verifier**

Run from the repository root:

```bash
node /home/mforce/.agents/feature-driver-artifacts/cluckwork-595/verify-route-chunks.mjs /home/mforce/dev/cluckwork
```

Expected: exit 1 naming all 20 routes as not declared by `React.lazy`. A parser/module error is not the expected red and must be reported.

- [ ] **Step 2: Replace only the 20 static content imports with direct named-export lazy adapters**

Before changing imports, audit all 20 modules for import-time behavior. Read every module's imports and
module-scope statements and record one result per file. The acceptable result is declarations only:
types, constants with pure literal/object/array/regex initializers, and function/component declarations.
Stop before conversion if any module performs a call, assignment, listener/registry setup, storage/global
write, top-level `await`, or side-effect-only CSS/module import required at startup. Use this candidate
scan to focus the read, but do not treat an empty regex result as the audit itself:

Owner-approved audit exemption (2026-08-29): `SettingsPage.tsx` may retain its existing module-scope
`TIME_ZONES` IIFE calling `Intl.supportedValuesOf("timeZone")`. It is a read-only capability query with
no global mutation or startup dependency; lazy loading intentionally defers it until Settings first opens.
This exemption does not cover any other call or any write, listener, registration, storage access,
top-level `await`, or startup-required side effect.

```bash
rg -n '^(await\b|[A-Za-z_$][A-Za-z0-9_$.]*\(|const [A-Za-z_$][A-Za-z0-9_$]*\s*=\s*(new\s+|[A-Za-z_$][A-Za-z0-9_$.]*\())' \
  web/src/routes/{Dashboard,DailyEntryPage,StockPage,CustomersPage,SalesPage,HistoryPage,GradesPage,ProductsPage,FlocksPage,InventoryPage,FeedPage,WaterPage,ExpensesPage,ReportsPage,AuditPage,ExportPage,UsersPage,SettingsPage,AccountPage,HelpPage}.tsx
rg -n "^import\\s+['\\\"]" \
  web/src/routes/{Dashboard,DailyEntryPage,StockPage,CustomersPage,SalesPage,HistoryPage,GradesPage,ProductsPage,FlocksPage,InventoryPage,FeedPage,WaterPage,ExpensesPage,ReportsPage,AuditPage,ExportPage,UsersPage,SettingsPage,AccountPage,HelpPage}.tsx
```

Add `lazy` and retain every eager shell import:

```tsx
import { lazy } from "react";
import { BrowserRouter, Navigate, Route, Routes } from "react-router";
import { AuthProvider } from "./auth/AuthContext";
import { ErrorBoundary } from "./components/ErrorBoundary";
import { UpdatePrompt } from "./pwa/UpdatePrompt";
import { SessionProvider } from "./session/SessionContext";
import { ProtectedRoute } from "./routes/ProtectedRoute";
import { AppLayout } from "./routes/AppLayout";
import { Login } from "./routes/Login";

const Dashboard = lazy(() => import("./routes/Dashboard").then(({ Dashboard }) => ({ default: Dashboard })));
const DailyEntryPage = lazy(() => import("./routes/DailyEntryPage").then(({ DailyEntryPage }) => ({ default: DailyEntryPage })));
const StockPage = lazy(() => import("./routes/StockPage").then(({ StockPage }) => ({ default: StockPage })));
const CustomersPage = lazy(() => import("./routes/CustomersPage").then(({ CustomersPage }) => ({ default: CustomersPage })));
const SalesPage = lazy(() => import("./routes/SalesPage").then(({ SalesPage }) => ({ default: SalesPage })));
const HistoryPage = lazy(() => import("./routes/HistoryPage").then(({ HistoryPage }) => ({ default: HistoryPage })));
const GradesPage = lazy(() => import("./routes/GradesPage").then(({ GradesPage }) => ({ default: GradesPage })));
const ProductsPage = lazy(() => import("./routes/ProductsPage").then(({ ProductsPage }) => ({ default: ProductsPage })));
const FlocksPage = lazy(() => import("./routes/FlocksPage").then(({ FlocksPage }) => ({ default: FlocksPage })));
const InventoryPage = lazy(() => import("./routes/InventoryPage").then(({ InventoryPage }) => ({ default: InventoryPage })));
const FeedPage = lazy(() => import("./routes/FeedPage").then(({ FeedPage }) => ({ default: FeedPage })));
const WaterPage = lazy(() => import("./routes/WaterPage").then(({ WaterPage }) => ({ default: WaterPage })));
const ExpensesPage = lazy(() => import("./routes/ExpensesPage").then(({ ExpensesPage }) => ({ default: ExpensesPage })));
const ReportsPage = lazy(() => import("./routes/ReportsPage").then(({ ReportsPage }) => ({ default: ReportsPage })));
const AuditPage = lazy(() => import("./routes/AuditPage").then(({ AuditPage }) => ({ default: AuditPage })));
const ExportPage = lazy(() => import("./routes/ExportPage").then(({ ExportPage }) => ({ default: ExportPage })));
const UsersPage = lazy(() => import("./routes/UsersPage").then(({ UsersPage }) => ({ default: UsersPage })));
const SettingsPage = lazy(() => import("./routes/SettingsPage").then(({ SettingsPage }) => ({ default: SettingsPage })));
const AccountPage = lazy(() => import("./routes/AccountPage").then(({ AccountPage }) => ({ default: AccountPage })));
const HelpPage = lazy(() => import("./routes/HelpPage").then(({ HelpPage }) => ({ default: HelpPage })));
```

Do not modify the `<Routes>` tree. Do not default-export route modules and do not factor the repeated adapter into a helper.

- [ ] **Step 3: Typecheck, test, and build the real production graph**

```bash
cd web
npm run typecheck
npm test -- --run src/routes/AppLayout.test.tsx
npm run build
```

Expected: all commands pass; the build emits the entry plus route chunks and no longer reports the route-weight >500 kB warning. If the warning remains, stop and report sizes.

- [ ] **Step 4: Prove every exact route and adapter is lazy-only in the emitted graph**

```bash
node /home/mforce/.agents/feature-driver-artifacts/cluckwork-595/verify-route-chunks.mjs /home/mforce/dev/cluckwork
```

Expected: `[route chunks] 20 authenticated routes are lazy-only and emitted outside the entry asset.`

- [ ] **Step 5: Execute the route-verifier mutations**

Use `apply_patch`, one mutation at a time, and run the verifier after each:

1. Delete the `expenses` `<Route>` and its lazy declaration: expect a named `expenses: approved route is missing` failure.
2. Rename `path="expenses"` to `path="expense"`: expect both missing `expenses` and unexpected `expense` failures.
3. Make `HelpPage` import/adapt `ProductsPage`: expect the verifier to name the module/component adapter mismatch.
4. Add `import { Dashboard as EagerDashboard } from "./routes/Dashboard";` while retaining the lazy Dashboard route, then add the temporary top-level side effect `console.log(EagerDashboard);` so Rollup cannot discard the import. After rebuilding, expect the verifier to report Dashboard in the eager entry map.
5. Insert a second `<Route path="expenses" element={<ReportsPage />} />` before the canonical Expenses route: expect a route-count failure plus `expenses: duplicate route registrations` naming both components.

Restore after each mutation, rebuild when source-map placement changes, then run the verifier green again. Mutations are verification edits and do not count against the driver's zero shipped-code fix budget.

- [ ] **Step 6: Checkpoint the route increment**

```bash
git diff --check -- web/src/App.tsx
git status --short -- web/src/App.tsx
```

If and only if commits are authorized and the current branch is not `main`, commit this file as `feat(web): split authenticated routes into lazy chunks`. Otherwise leave it unstaged.

---

### Task 3: Make generated PWA verification exhaustive over emitted JavaScript

**Files:**
- Modify: `web/scripts/verify-sw.mjs:17-129`

**Interfaces:**
- Consumes: `web/dist/sw.js` generated by Workbox and `web/dist/assets/*.js` generated by Vite.
- Produces: a verifier that extracts URLs only from the `precacheAndRoute([...])` array and compares exact normalized asset paths.

- [ ] **Step 1: Demonstrate the current false green against a copied generated worker**

After Task 2's build, create a uniquely named temporary copy:

```bash
cd web
mutant_dir="$(mktemp -d /tmp/cluckwork-595-sw.XXXXXX)"
cp -a dist/. "$mutant_dir/"
find "$mutant_dir/assets" -maxdepth 1 -type f -name '*.js' -printf '%f\n' | sort
```

Resolve one route chunk name from that output, inspect its exact `{url:"assets/<name>.js",revision:null}` entry in `$mutant_dir/sw.js`, and remove only that object with `apply_patch`. Run:

```bash
node scripts/verify-sw.mjs "$mutant_dir"
```

Expected before the guard change: exit 0 despite the missing route chunk URL. Record the removed exact asset name.

- [ ] **Step 2: Bound precache extraction to Workbox's actual call array**

Import `readdirSync`:

```js
import { readFileSync, existsSync, readdirSync } from "node:fs";
```

Add a scanner that never evaluates generated JavaScript and ignores `url:` fields outside the precache array:

```js
function extractPrecacheUrls(source) {
  const marker = "precacheAndRoute(";
  const callAt = source.indexOf(marker);
  if (callAt < 0) return null;

  let start = callAt + marker.length;
  while (/\s/.test(source[start] ?? "")) start++;
  if (source[start] !== "[") return null;

  let depth = 0;
  let quote = null;
  let escaped = false;
  for (let i = start; i < source.length; i++) {
    const c = source[i];
    if (quote !== null) {
      if (escaped) escaped = false;
      else if (c === "\\") escaped = true;
      else if (c === quote) quote = null;
      continue;
    }
    if (c === "\"" || c === "'") { quote = c; continue; }
    if (c === "[") depth++;
    if (c === "]" && --depth === 0) {
      const raw = source.slice(start, i + 1);
      return [...raw.matchAll(/url:"([^"]+)"/g)].map(([, url]) => url);
    }
  }
  return null;
}
```

- [ ] **Step 3: Compare exact normalized precache URLs with every emitted JS asset**

Replace the global `url:` extraction with:

```js
const extractedPrecache = extractPrecacheUrls(sw);
check(extractedPrecache !== null, "no usable precacheAndRoute array found in the generated worker");
const precached = extractedPrecache ?? [];
const normalizedPrecache = new Set(precached.map((url) => url.replace(/^\.?\//, "")));
const emittedJs = readdirSync(join(dist, "assets"), { withFileTypes: true })
  .filter((entry) => entry.isFile() && entry.name.endsWith(".js"))
  .map((entry) => `assets/${entry.name}`)
  .sort();
const missingJs = emittedJs.filter((asset) => !normalizedPrecache.has(asset));

const apiish = precached.filter((url) => /(^|\/)api(\/|$)/i.test(url));
check(apiish.length === 0, `API paths found in the precache manifest: ${apiish.join(", ")}`);
check(precached.length > 0, "precache manifest is empty — the app shell would not be cached at all");
check(missingJs.length === 0, `emitted JavaScript missing from precache: ${missingJs.join(", ")}`);
```

Retain the existing denylist and no-runtime-cache checks. Update the success message to include `${emittedJs.length} JavaScript assets verified` without weakening its current guarantees.

- [ ] **Step 4: Prove the copied-worker mutation turns red, then run the real worker green**

```bash
node scripts/verify-sw.mjs "$mutant_dir"
npm run verify:sw
```

Expected: the copied worker exits 1 and names the exact removed `assets/<route>.js`; the untouched `dist` exits 0. Add an unrelated `url:"assets/<route>.js"` outside `precacheAndRoute` in the copied worker and confirm it remains red, proving extraction is bounded to the manifest.

- [ ] **Step 5: Checkpoint the PWA guard increment**

```bash
git diff --check -- web/scripts/verify-sw.mjs
git status --short -- web/scripts/verify-sw.mjs
```

If and only if commits are authorized and the current branch is not `main`, commit this file as `test(web): require every JS chunk in PWA precache`. Otherwise leave it unstaged.

---

### Task 4: Document the brief page-loading state everywhere the repository requires

**Files:**
- Modify: `web/src/routes/HelpPage.tsx:104-122,548-575`
- Modify: `web/src/routes/HelpPage.test.tsx`
- Modify: `web/src/i18n/en.ts:2234-2255,2903-2934`
- Modify: `web/src/i18n/es.ts` in the matching Help sections
- Modify: `web/src/i18n/tl.ts` in the matching Help sections
- Modify: `specs/product/GLOSSARY.md:48-70`

**Interfaces:**
- Consumes: the existing Help Getting around list, in-app glossary table, and en-as-source catalog parity convention.
- Produces: `gettingAroundPageLoading`, `glossaryPageLoadingTerm`, and `glossaryPageLoadingDef` in every catalog, plus one product-glossary entry.

- [ ] **Step 1: Add failing Help behavior and catalog-parity tests**

Add a behavior test near the other Getting around cases:

```tsx
it("documents page loading in Getting around and the in-app glossary (#595)", () => {
  render(<HelpPage />);
  expect(screen.getByText(/first time you open a screen after starting or updating Cluckwork/i))
    .toBeInTheDocument();
  expect(screen.getByRole("rowheader", { name: "Page loading" })).toBeInTheDocument();

  for (const catalog of [es, tl]) {
    expect(catalog.help.gettingAroundPageLoading).toBeTruthy();
    expect(catalog.help.gettingAroundPageLoading).not.toBe(en.help.gettingAroundPageLoading);
    expect(catalog.help.glossaryPageLoadingTerm).not.toBe(en.help.glossaryPageLoadingTerm);
    expect(catalog.help.glossaryPageLoadingDef).not.toBe(en.help.glossaryPageLoadingDef);
  }
});
```

Inside the existing `HelpPage i18n wiring (#182, Task 32)` describe, use its `withOverride` helper so a
hardcoded English list item cannot pass:

```tsx
it("reads the Page loading note from the catalog", () => {
  withOverride("gettingAroundPageLoading", "PAGE-LOADING-NOTE-MARKER", () => {
    render(<HelpPage />);
    expect(screen.getByText("PAGE-LOADING-NOTE-MARKER")).toBeInTheDocument();
    expect(screen.queryByText(/first time you open a screen after starting or updating Cluckwork/i))
      .not.toBeInTheDocument();
  });
});
```

Inside the existing glossary i18n-wiring describe, use its `withOverride` helper:

```tsx
it("reads the Page loading glossary row from the catalog", () => {
  withOverride("glossaryPageLoadingTerm", "PAGE-LOADING-TERM-MARKER", () => {
    withOverride("glossaryPageLoadingDef", "PAGE-LOADING-DEF-MARKER", () => {
      render(<HelpPage />);
      expect(screen.getByRole("rowheader", { name: "PAGE-LOADING-TERM-MARKER" })).toBeInTheDocument();
      expect(screen.getByText("PAGE-LOADING-DEF-MARKER")).toBeInTheDocument();
    });
  });
});
```

Run and expect missing-key/type failures:

```bash
cd web
npm test -- --run src/routes/HelpPage.test.tsx src/i18n/catalogParity.test.ts
```

- [ ] **Step 2: Render the new Help list item and glossary row**

Append the Getting around list item after the phone-tabs item:

```tsx
<li>{t("gettingAroundPageLoading")}</li>
```

Add the glossary row immediately after Navigation:

```tsx
<tr><th scope="row">{t("glossaryPageLoadingTerm")}</th>
  <td>{t("glossaryPageLoadingDef")}</td></tr>
```

- [ ] **Step 3: Add exact English, Spanish, and Tagalog catalog entries**

Add these keys to the corresponding Help sections in each catalog:

```ts
// en.ts
gettingAroundPageLoading:
  "The first time you open a screen after starting or updating Cluckwork, a brief page-loading message may appear while that screen opens. Navigation stays available; wait for the screen to appear.",
glossaryPageLoadingTerm: "Page loading",
glossaryPageLoadingDef:
  "The brief message shown while Cluckwork opens a screen that has not loaded yet. Navigation remains available, and the message disappears when the screen is ready.",

// es.ts
gettingAroundPageLoading:
  "La primera vez que abra una pantalla después de iniciar o actualizar Cluckwork, puede aparecer brevemente un mensaje de carga mientras se abre esa pantalla. La navegación sigue disponible; espere a que aparezca la pantalla.",
glossaryPageLoadingTerm: "Carga de página",
glossaryPageLoadingDef:
  "El breve mensaje que aparece mientras Cluckwork abre una pantalla que aún no se ha cargado. La navegación sigue disponible y el mensaje desaparece cuando la pantalla está lista.",

// tl.ts
gettingAroundPageLoading:
  "Sa unang pagkakataong magbukas ka ng screen pagkatapos simulan o i-update ang Cluckwork, maaaring sandaling lumabas ang mensahe ng pag-load habang binubuksan ang screen. Magagamit pa rin ang navigation; hintaying lumabas ang screen.",
glossaryPageLoadingTerm: "Pag-load ng page",
glossaryPageLoadingDef:
  "Ang maikling mensaheng ipinapakita habang binubuksan ng Cluckwork ang isang screen na hindi pa nalo-load. Magagamit pa rin ang navigation, at nawawala ang mensahe kapag handa na ang screen.",
```

- [ ] **Step 4: Add the product-glossary concept under Getting around**

Insert after Navigation and before the error-screen entry:

```markdown
**Page loading** — the brief **Loading…** state that may appear the first time
a screen opens in the current app version. Only the content pane waits; the
sidebar or bottom navigation stays available. The message disappears when the
screen is ready, and a failed page chunk uses the existing **"Something went
wrong" screen** instead of blanking the app.
```

- [ ] **Step 5: Run Help, catalog parity, and typecheck green**

```bash
cd web
npm test -- --run src/routes/HelpPage.test.tsx src/i18n/catalogParity.test.ts
npm run typecheck
```

Expected: all tests pass and all three catalogs retain exact key/tag parity.

- [ ] **Step 6: Checkpoint the documentation increment**

```bash
git diff --check -- web/src/routes/HelpPage.tsx web/src/routes/HelpPage.test.tsx \
  web/src/i18n/en.ts web/src/i18n/es.ts web/src/i18n/tl.ts specs/product/GLOSSARY.md
git status --short -- web/src/routes/HelpPage.tsx web/src/routes/HelpPage.test.tsx \
  web/src/i18n/en.ts web/src/i18n/es.ts web/src/i18n/tl.ts specs/product/GLOSSARY.md
```

If and only if commits are authorized and the current branch is not `main`, commit these files as `docs(web): explain lazy page loading`. Otherwise leave them unstaged.

---

### Task 5: Verify the complete production slice and stop on scope drift

**Files:**
- Verify only: all eleven approved implementation files and the plan artifact

**Interfaces:**
- Consumes: Tasks 1-4 as one production SPA build.
- Produces: merge-review evidence at one exact head SHA; no new implementation surface.

- [ ] **Step 1: Confirm the diff is bounded**

```bash
git status --short
git diff --name-only 0955095f3185471a55a6890adfa827bb29dd518e
git ls-files --others --exclude-standard
```

`git diff <base>` includes both staged and unstaged tracked edits, whether or not commits were authorized. Expected implementation files are exactly the eleven listed under Global Constraints. The plan itself may appear as the sole additional repository artifact; any other implementation file is a stop-and-report scope change.

- [ ] **Step 2: Run the frontend CI bar against the final tree**

```bash
cd web
npm run typecheck
npm run test:coverage
npm run build
npm run verify:sw
```

Expected: all pass; coverage remains above configured thresholds; build emits route chunks and no route-weight >500 kB warning; every JS asset is reported precached.

- [ ] **Step 3: Run the exact production-route verifier**

```bash
node /home/mforce/.agents/feature-driver-artifacts/cluckwork-595/verify-route-chunks.mjs /home/mforce/dev/cluckwork
```

Expected: all 20 approved authenticated routes are lazy-only and outside the entry source map.

- [ ] **Step 4: Run the real built-SPA smoke suite**

From the repository root, use the existing seeded simulation stack. If it is not running, follow `tools/simulation/ui/README.md` exactly (`bootstrap.sh`, then `reset.sh`) rather than inventing credentials or bypassing preflight:

```bash
cd tools/simulation/ui
npm run typecheck
npm test
```

Expected: the real SPA smoke suite passes. A missing/down simulation stack is reported as an environment blocker, not represented as a green run.

- [ ] **Step 5: Record final measurements and commit any plan-only tracking update**

Record at the final head SHA:

- Vitest file/test counts and coverage result.
- Entry JS raw/gzip size and the emitted JS chunk count.
- Route verifier's exact success line.
- Service-worker verifier's exact asset count.
- Playwright pass/skip counts, or the explicit environment blocker.
- `git status --short` and exact changed-file list.

Do not modify product code during this evidence step. Any failure returns to the implementer as a new increment because the driver fix budget is zero.
