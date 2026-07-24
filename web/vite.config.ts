/// <reference types="vitest/config" />
import { defineConfig } from "vitest/config";
import { loadEnv } from "vite";
import react from "@vitejs/plugin-react";
import { VitePWA } from "vite-plugin-pwa";

// Dev-only proxy: the SPA calls same-origin "/api/..." and Vite forwards to the
// backend, so no CORS config is needed on the API for local dev. Override the
// target with VITE_API_TARGET (defaults to the docker-compose port 8080).
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "");
  const target = env.VITE_API_TARGET ?? "http://localhost:8080";
  return {
    plugins: [
      react(),
      // #142 — installable PWA. Scope is the app SHELL only: the service worker
      // makes the SPA launchable from a home screen and survivable on a bad
      // connection. It caches no application data — offline capture is #50.
      VitePWA({
        // 'prompt', not 'autoUpdate': this app is used to type daily entries on
        // barn phones, and skipWaiting can swap the running app out mid-form.
        // The new shell installs in the background and waits for the user to
        // accept (see UpdatePrompt), so an update never eats in-progress work.
        registerType: "prompt",
        // We register by hand in registerServiceWorker.ts so registration can be
        // guarded on a secure context; the plugin's auto-injected snippet has no
        // such guard.
        injectRegister: null,
        // No `includeAssets`: the workbox globPatterns below already sweep up
        // every png/svg in the build, and listing them twice puts duplicate
        // entries in the precache manifest.
        manifest: {
          name: "Cluckwork",
          short_name: "Cluckwork",
          description: "Poultry egg-farm management",
          start_url: "/",
          scope: "/",
          display: "standalone",
          // Matches the light-scheme theme-color already in index.html; the
          // manifest takes a single value, so the light aubergine is the one
          // that shows in the task switcher and splash.
          theme_color: "#4a154b",
          background_color: "#4a154b",
          icons: [
            { src: "/icon-192.png", sizes: "192x192", type: "image/png" },
            { src: "/icon-512.png", sizes: "512x512", type: "image/png" },
            // Separate maskable art: Android crops to a circle/squircle, and the
            // standard mark is full-bleed, so it needs its own padded variant.
            { src: "/icon-192-maskable.png", sizes: "192x192", type: "image/png", purpose: "maskable" },
            { src: "/icon-512-maskable.png", sizes: "512x512", type: "image/png", purpose: "maskable" },
          ],
        },
        workbox: {
          // The built shell: hashed JS/CSS plus the root entry and icons.
          globPatterns: ["**/*.{js,css,html,svg,png,woff2}"],
          // An unknown route serves index.html from the cache, EXCEPT under
          // /api — those must always reach the network.
          navigateFallback: "/index.html",
          navigateFallbackDenylist: [/^\/api\//],
          // Belt to that braces: never let a runtime handler answer an /api
          // request from cache. Auth state and tenant data are per-request; a
          // stale shared response here would be a correctness bug, not a
          // performance win. #50 adds an explicit, deliberate offline path.
          runtimeCaching: [],
          navigationPreload: false,
        },
        // The SW is a production artifact; leaving it off in dev keeps `npm run
        // dev` free of stale-cache confusion.
        devOptions: { enabled: false },
      }),
    ],
    server: {
      port: 5173,
      proxy: {
        "/api": { target, changeOrigin: true },
      },
    },
    // Unit tests only (Vitest). E2E stays the manual Playwright drill (#105).
    // Explicit vitest imports in each test — no globals — so the app's strict
    // tsconfig stays clean of test-runner ambient types.
    test: {
      environment: "jsdom",
      setupFiles: ["./src/test/setup.ts"],
      globals: false,
      include: ["src/**/*.test.{ts,tsx}"],
      // #121: coverage gate. The thresholds are a REGRESSION FLOOR near the
      // current numbers, not a target — the global floor is deliberately low
      // (the SPA still has ~14 untested screens) and ratchets up as each
      // screen's tests land. The per-directory locks pin the already-covered
      // foundation (auth/dates/api-client) at ~100% so it can't backslide.
      coverage: {
        provider: "v8",
        include: ["src/**/*.{ts,tsx}"],
        exclude: [
          "src/main.tsx", // app entry: renders <App/>, nothing to unit-test
          "src/api/types.ts", // type-only DTOs (no runtime code)
          "src/**/*.d.ts", // ambient declarations (vite-env.d.ts, etc.)
          "src/**/*.test.{ts,tsx}",
          "src/test/**", // test-only helpers (renderWithProviders, jwt, setup)
        ],
        reporter: ["text", "html"],
        thresholds: {
          // Global regression floor, re-baselined as screen tests land. After
          // the farm settings screen + farm context (#123): lines 91.3 /
          // branch 85.3 / funcs 71.9. Reports stays untested; the static
          // Help/shell screens are excluded-in-spirit — hence sub-100. A
          // screen-test PR raises lines/functions; branches move either way
          // (testing a screen exposes all its conditional branches), so
          // re-baseline branches in BOTH directions with headroom.
          lines: 91,
          statements: 91,
          functions: 71,
          branches: 85,
          // high-water locks on the fully-covered foundation
          "src/auth/**": { statements: 100, lines: 100, functions: 100, branches: 95 },
          "src/lib/**": { statements: 100, lines: 100, functions: 100, branches: 100 },
          // The farm context joins them (#123): every screen with a date field
          // now reads its timezone through it, so a hole here is a hole in all
          // of them at once.
          "src/farm/**": { statements: 100, lines: 100, functions: 100, branches: 100 },
          "src/api/client.ts": { statements: 95, lines: 95, functions: 100, branches: 85 },
        },
      },
    },
  };
});
