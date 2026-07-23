# Cluckwork Web

React + Vite + TypeScript SPA for Cluckwork, consuming the JSON API
(`Cluckwork.Api`). Phase 1.0 MVP client.

## Stack

- React 19 + React Router 7
- Vite 6 (dev server + build)
- TypeScript (strict)
- No CSS framework yet — plain CSS in `src/styles.css`

## Running

```bash
cp .env.example .env      # set VITE_API_TARGET if the API is not on :8080
npm install
npm run dev               # http://localhost:5173
```

The dev server proxies `/api/*` to the backend (see `vite.config.ts`), so the
SPA is same-origin with the API and needs no CORS config. Point
`VITE_API_TARGET` at wherever the API runs (docker-compose: `:8080`;
`dotnet run`: the `ASPNETCORE_URLS` port).

You need a user to log in with — the API seeds a default admin on startup once
[#5](../specs/product/specs.md) lands. Until then, create one via the
integration-test path or Swagger.

## Scripts

- `npm run dev` — dev server
- `npm run build` — typecheck + production build to `dist/`
- `npm run typecheck` — types only
- `npm run test` — run the unit suite once (Vitest)
- `npm run test:coverage` — run the suite with the coverage gate (what CI runs)
- `npm run test:watch` — Vitest watch mode
- `npm run preview` — serve the built bundle

## Tests

Unit tests run on [Vitest](https://vitest.dev) + Testing Library in a jsdom
environment; setup lives in `src/test/setup.ts` (jest-dom matchers + per-test
cleanup). Tests sit next to the code they cover as `*.test.ts(x)`, and CI runs
`npm run test:coverage` on every PR (`.github/workflows/ci.yml`).

**New `web/` code ships with tests in the same PR** — components, hooks, utils,
and API-layer logic all need Vitest coverage. A coverage gate (`vite.config.ts`
→ `test.coverage.thresholds`) enforces it: a global regression floor that
ratchets up as screens gain tests, plus per-directory high-water locks that hold
the fully-covered foundation (`src/auth`, `src/lib`, `src/api/client.ts`) at
~100% so it can't backslide. Raise the global floor in the same PR that adds a
screen's tests.

- Import `describe`/`it`/`expect` explicitly from `vitest` — globals are off so
  the app's strict tsconfig stays free of test-runner ambient types.
- Start with pure, high-value logic (auth/role decode, money formatting). The
  current suite covers `auth/claims.ts` role precedence + malformed-token
  decoding and `formatMoney` currency scaling. Shared test helpers live in
  `src/test/` (e.g. `jwt.ts` for seeding a decoded session).
- End-to-end coverage stays the manual Playwright drill for now; a CI E2E
  harness is a separate later slice.

## Structure

```
src/
  api/       fetch client (auth header + transparent refresh) + types
  auth/      token store, AuthContext, useAuth
  routes/    Login, ProtectedRoute, AppLayout shell, screens
```

## Auth flow

`login` → `POST /api/v1/auth/login` → the **access token lives in JS memory only**
(never localStorage/sessionStorage) and the **refresh token is an HttpOnly cookie**
the browser attaches automatically (#145). Authenticated requests attach the
bearer access token; a 401 triggers one transparent `POST /api/v1/auth/refresh`
(cookie-carried, `X-Cluckwork-Auth` header) + retry (single-flight). On page load
the session is restored by a silent refresh against the cookie; if that fails the
router bounces to `/login`. `logout` revokes server-side and expires the cookie.

Refresh is serialised **across tabs** via the Web Locks API (#169): the refresh
token lives only in the shared cookie, so two tabs refreshing at once would each
present the same value and the second would trip the server's rotation/reuse
detection, revoking the family and logging both tabs out. The lock lets one tab
refresh at a time; the next tab then presents the freshly-rotated cookie. A hung
refresh can't park the lock forever — it's aborted after a bounded timeout so
other tabs recover. Server reuse-detection stays strict; browsers without
`navigator.locks` fall back to the per-tab single-flight only.

One residual a page-owned lock can't close: if a tab is closed in the sub-second
between sending a refresh and receiving the rotated cookie, the lock releases
while the cookie is still stale, so the next tab can still trip reuse-detection.
Narrow; the full fix needs an idempotent server refresh (tracked in #176).
