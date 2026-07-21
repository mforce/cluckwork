/// <reference types="vitest/config" />
import { defineConfig } from "vitest/config";
import { loadEnv } from "vite";
import react from "@vitejs/plugin-react";

// Dev-only proxy: the SPA calls same-origin "/api/..." and Vite forwards to the
// backend, so no CORS config is needed on the API for local dev. Override the
// target with VITE_API_TARGET (defaults to the docker-compose port 8080).
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "");
  const target = env.VITE_API_TARGET ?? "http://localhost:8080";
  return {
    plugins: [react()],
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
          "src/vite-env.d.ts",
          "src/**/*.test.{ts,tsx}",
          "src/test/**", // test-only helpers (renderWithProviders, jwt, setup)
        ],
        reporter: ["text", "html"],
        thresholds: {
          // global regression floor (main ≈ lines 15.6 / branch 79.7 / funcs 30.3)
          lines: 15,
          statements: 15,
          functions: 28,
          branches: 78,
          // high-water locks on the fully-covered foundation
          "src/auth/**": { statements: 100, lines: 100, functions: 100, branches: 95 },
          "src/lib/**": { statements: 100, lines: 100, functions: 100, branches: 100 },
          "src/api/client.ts": { statements: 95, lines: 95, functions: 100, branches: 85 },
        },
      },
    },
  };
});
