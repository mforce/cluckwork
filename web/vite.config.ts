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
      restoreMocks: true,
      include: ["src/**/*.test.{ts,tsx}"],
    },
  };
});
