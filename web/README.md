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
- `npm run preview` — serve the built bundle

## Structure

```
src/
  api/       fetch client (auth header + transparent refresh) + types
  auth/      token store, AuthContext, useAuth
  routes/    Login, ProtectedRoute, AppLayout shell, screens
```

## Auth flow

`login` → `POST /api/v1/auth/login` → tokens in localStorage. Authenticated
requests attach the bearer token; a 401 triggers one transparent
`POST /api/v1/auth/refresh` + retry (single-flight). If refresh fails, tokens
clear and the router bounces to `/login`.
