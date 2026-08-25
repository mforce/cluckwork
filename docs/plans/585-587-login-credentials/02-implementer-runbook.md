# #587 remembered-farm removal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an operator deliberately forget one remembered farm without clearing unrelated preferences, while adding stable identifiers to the three existing login controls.

**Architecture:** `farmCodeCache.ts` owns canonical, best-effort local-storage mutation behind the existing Web Locks protocol. `Login` owns the confirmation, visible picker state, and a post-removal focus fallback. The server request and existing autocomplete tokens remain untouched.

**Tech Stack:** React 19, TypeScript, Vitest/Testing Library, PostCSS, i18next.

**Spec:** `docs/plans/585-587-login-credentials/01-design.md`

## Global constraints

- Work only on `feat/remembered-farm-forget`; do not commit to `main`.
- Do not touch `src/Cluckwork.Api/appsettings.Development.json`; it is a pre-existing user change.
- Do not modify the API, server code, migrations, login body, or the existing `autocomplete="username"` / `autocomplete="current-password"` tokens.
- Keep `?farm=` precedence: a valid URL code must avoid reading and rendering the cache picker.
- `removeFarmCode(value: string): Promise<void>` must never reject. A successfully acquired Web Lock serializes writes; absent/rejected locks remain best-effort, unsynchronised fallback.
- Update all three supported UI catalogs (`en`, `es`, `tl`), the product glossary, Help copy, and in-app glossary.
- Add no dependency. Run all web commands from `web/`.

## File map

- `web/src/auth/farmCodeCache.ts` — canonical, lock-aware roster removal.
- `web/src/auth/farmCodeCache.test.ts` — raw-storage, fallback, and Web Locks guards.
- `web/src/routes/Login.tsx` — mutable picker state, confirm gate, focus fallback, and input identifiers.
- `web/src/routes/Login.test.tsx` — picker/confirmation/focus/identifier behavior.
- `web/src/routes/Login.styles.test.ts` — stylesheet guard for the Forget touch target.
- `web/src/styles.css` — picker entry layout and 44px Forget target.
- `web/src/i18n/{en,es,tl}.ts` — auth confirmation, Help, and glossary copy.
- `specs/product/GLOSSARY.md` — revocable roster disclosure.

---

### Task 1: lock-aware cache removal

**Files:**
- Modify: `web/src/auth/farmCodeCache.ts`
- Modify: `web/src/auth/farmCodeCache.test.ts`

**Interface:** Export `async function removeFarmCode(value: string): Promise<void>`.

- [ ] **Step 1: Write the failing cache tests.** Import `removeFarmCode`. Seed raw JSON and await removal:

```ts
it("removes only the requested canonical farm from raw storage", async () => {
  localStorage.setItem(KEY, JSON.stringify(["farm-a", "farm-b", "farm-c"]));
  await removeFarmCode(" Farm-B ");
  expect(JSON.parse(localStorage.getItem(KEY) ?? "[]")).toEqual(["farm-a", "farm-c"]);
});

it("removes the only remembered farm from raw storage", async () => {
  localStorage.setItem(KEY, JSON.stringify(["farm-a"]));
  await removeFarmCode("farm-a");
  expect(JSON.parse(localStorage.getItem(KEY) ?? "[]")).toEqual([]);
});

it("removeFarmCode is best-effort when storage throws", async () => {
  const spy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
    throw new Error("quota exhausted");
  });
  await expect(removeFarmCode("farm-a")).resolves.toBeUndefined();
  spy.mockRestore();
});
```

Also add a Web Locks test using the existing `stubLocks`: assert `removeFarmCode("farm-b")` calls `request` with `cluckwork.farmCodes.write`, re-reads a simulated intervening `farm-c` inside the callback, and leaves `farm-c` in raw JSON. Add the rejected-lock test: it resolves and writes the filtered raw array. Do not assert cross-tab ordering for absent/rejected locks.

- [ ] **Step 2: Run the focused cache suite red.**

```bash
npm test -- farmCodeCache.test.ts
```

Expected: TypeScript import failure because `removeFarmCode` does not exist.

- [ ] **Step 3: Implement the minimal writer.** Keep the existing `ROSTER_LOCK` and clone `rememberFarmCode`'s read-inside-callback / `navigator.locks` / catch-fallback shape. The writer must be exactly equivalent in behavior to:

```ts
export async function removeFarmCode(value: string): Promise<void> {
  const code = canonicalFarmCode(value);
  if (code === null) return;
  const write = (): void => {
    const next = readFarmCodes().filter((candidate) => candidate !== code);
    try {
      localStorage.setItem(KEY, JSON.stringify(next));
    } catch {
      // A forgotten local convenience must never surface as an auth failure.
    }
  };
  const locks: LockManager | undefined = globalThis.navigator?.locks;
  if (locks === undefined) {
    write();
    return;
  }
  try {
    await locks.request(ROSTER_LOCK, write);
  } catch {
    write();
  }
}
```

- [ ] **Step 4: Re-run focused cache suite green.**

```bash
npm test -- farmCodeCache.test.ts
```

Expected: all `farmCodeCache` tests pass.

### Task 2: deliberate picker removal and stable login fields

**Files:**
- Modify: `web/src/routes/Login.tsx`
- Modify: `web/src/routes/Login.test.tsx`

**Interface:** `Login` renders a picker for one-or-more cached farms and does not change `login(farmCode, email, password)`.

- [ ] **Step 1: Write failing Login tests.** Replace the current one-code “no picker” assertion with an accessible group assertion. Add tests that:

```ts
it("does not remove a remembered farm until its confirmation is accepted", async () => {
  localStorage.setItem("cluckwork.farmCodes", JSON.stringify(["farm-a"]));
  renderWithProviders(tree(), { route: "/login", token: null });
  await screen.findByRole("button", { name: "Sign in" });
  fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:forgetFarm", { farmCode: "farm-a" }) }));
  fireEvent.click(screen.getByRole("button", { name: i18n.t("common:cancel") }));
  expect(JSON.parse(localStorage.getItem("cluckwork.farmCodes") ?? "[]")).toEqual(["farm-a"]);
  expect(screen.getByRole("group", { name: i18n.t("auth:recentFarms") })).toBeInTheDocument();
});

it("forgets a selected prefilled farm, clears the field, and focuses it", async () => {
  localStorage.setItem("cluckwork.farmCodes", JSON.stringify(["farm-a"]));
  renderWithProviders(tree(), { route: "/login", token: null });
  await screen.findByRole("button", { name: "Sign in" });
  fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:forgetFarm", { farmCode: "farm-a" }) }));
  fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:forgetFarmConfirm") }));
  await waitFor(() => expect(farmField()).toHaveValue(""));
  expect(JSON.parse(localStorage.getItem("cluckwork.farmCodes") ?? "[]")).toEqual([]);
  expect(screen.queryByRole("group", { name: i18n.t("auth:recentFarms") })).not.toBeInTheDocument();
  await waitFor(() => expect(farmField()).toHaveFocus());
});
```

Add an identifier test that checks each exact pair: `#farm-code[name="farmCode"]`, `#email[name="email"]`, and `#current-password[name="password"]`; also assert the email and password retain their current autocomplete values. Update several-farm selection test to query the code selection button by its exact accessible name rather than all buttons beginning with the code.

- [ ] **Step 2: Run the focused Login suite red.**

```bash
npm test -- Login.test.tsx
```

Expected: the new translation keys and Forget controls are absent.

- [ ] **Step 3: Implement the minimal Login behavior.**

1. Import `useRef`, `useConfirm`, and `removeFarmCode`.
2. Make remembered codes mutable with `const [rememberedCodes, setRememberedCodes] = useState(...)`.
3. Add `const farmCodeInputRef = useRef<HTMLInputElement>(null);` and `const { confirm, confirmDialog } = useConfirm();`.
4. Implement `async function forgetFarm(code: string)`: await `confirm` with `t("forgetFarmTitle", { farmCode: code })`, `t("forgetFarmBody", { farmCode: code })`, `t("forgetFarmConfirm")`, and `destructive: true`; return on false; call `void removeFarmCode(code)`; filter page state; clear `farmCode` only through `setFarmCode((current) => current === code ? "" : current)`; then `requestAnimationFrame(() => farmCodeInputRef.current?.focus())`.
5. Render the picker when `rememberedCodes.length >= 1`, retaining the existing URL cache gate. Give each code a wrapper; keep the code-selection button’s accessible name exactly `code`; add a distinct `auth-forget-farm` button named `t("forgetFarm", { farmCode: code })` which calls `void forgetFarm(code)`.
6. Render `{confirmDialog}` inside `<main>` so the shared portal/dialog lifecycle stays mounted.
7. Add only these identifiers: farm input `id="farm-code" name="farmCode" ref={farmCodeInputRef}`; email `id="email" name="email"`; password `id="current-password" name="password"`. Preserve all values, onChange handlers, requirements, max lengths, and autocomplete values.

- [ ] **Step 4: Re-run focused Login suite green.**

```bash
npm test -- Login.test.tsx
```

Expected: all Login tests pass.

### Task 3: translated copy, documentation, and touch target

**Files:**
- Modify: `web/src/i18n/en.ts`
- Modify: `web/src/i18n/es.ts`
- Modify: `web/src/i18n/tl.ts`
- Modify: `specs/product/GLOSSARY.md`
- Modify: `web/src/styles.css`
- Create: `web/src/routes/Login.styles.test.ts`

- [ ] **Step 1: Add the four auth keys in every locale.** Use these English values and natural equivalent Spanish/Tagalog translations:

```ts
forgetFarm: "Forget {{farmCode}}",
forgetFarmTitle: "Forget {{farmCode}}?",
forgetFarmBody: "This removes {{farmCode}} from Recent farms on this device. It does not change your account or other device preferences.",
forgetFarmConfirm: "Forget farm",
```

In `signingInMultiTabResync` and `glossaryFarmCodeDef` in each catalog, say that each remembered farm can be removed with its Forget control and that doing so does not clear another farm session or unrelated device preferences. Update the product glossary’s description of the picker: a single remembered code now also has a picker entry, and each entry is individually revocable without clearing language/theme settings.

- [ ] **Step 2: Add explicit picker styles and the stylesheet guard.** Replace the broad direct-child selector with classes for the picker group and each entry. Give `.auth .auth-forget-farm` `min-block-size: 44px` and a visually distinct destructive treatment without making the farm-selection button destructive. `Login.styles.test.ts` must parse `src/styles.css` with PostCSS, construct a button matching `.auth .auth-forget-farm`, collect every matching rule at every nesting depth, fail if none match, and fail if any matching `min-block-size`/`min-height` declaration is below 44px. This guard must include media-query rules so a later phone override cannot shrink the target.

- [ ] **Step 3: Run the focused tests red then green.** Add the style test before the CSS declaration; it must fail for no matching Forget rule, then pass after the declaration.

```bash
npm test -- Login.styles.test.ts
npm test -- Login.test.tsx farmCodeCache.test.ts
```

Expected: all named tests pass after implementation.

### Task 4: full web verification

- [ ] **Step 1: Run typecheck, unit suite, coverage, build, service-worker verification, and i18n scan in the foreground.**

```bash
npm run typecheck
npm test
npm run test:coverage
npm run build
npm run verify:sw
npm run i18n:scan
```

Expected: every command exits 0; coverage thresholds remain unchanged.

- [ ] **Step 2: Perform the declared mutation checks.** Mark each temporary change with `// MUTANT M<n>`; the mutation must compile; run the named test red; restore; rerun green. Do not leave a marker (`rg -n "MUTANT" web/src` must be empty).

| Mutant | Expected red test |
| --- | --- |
| Replace `filter((candidate) => candidate !== code)` with `filter(() => true)` | raw-storage removal test |
| Bypass the `await confirm(...)` false branch | cancellation test |
| Remove the `current === code ? "" : current` branch | selected-prefilled clearing test |
| Remove `min-block-size: 44px` | `Login.styles.test.ts` |
| Remove each `id` and each `name` one at a time | identifier test |

## Tracker and PR actions

Before opening the PR, amend epic #530 to record the deliberate #585 decision: no standards-backed farm-qualified password-manager credential key exists, and the chosen scope deliberately leaves username/password fields and autocomplete tokens unchanged. Amend #585 to distinguish the stable `id`/`name` work shipped under #587 from its declined farm-qualified-manager requirement; point its close comment to the epic decision and close #585 as won’t-fix. Amend #587 to defer its ADR acceptance item to #537, the ADR/docs slice; do not claim an ADR change in this PR. These tracker edits belong in the same PR description/comment history.

## Handoff

Commit only after all verification and mutations are green, using `feat(web): let operators forget remembered farms`. Push `feat/remembered-farm-forget` and open a draft PR titled `feat(web): let operators forget remembered farms`, linking `Closes #587` and noting #585 as deliberate won’t-fix. Do not merge.
