// Vitest setup — runs once before each test file.
// Registers jest-dom matchers (.toBeInTheDocument etc.) on Vitest's expect and
// unmounts any rendered React tree after each test so localStorage/role state
// set in one case never bleeds into the next.
import "@testing-library/jest-dom/vitest";
import { afterEach } from "vitest";
import { cleanup } from "@testing-library/react";

afterEach(() => {
  cleanup();
  localStorage.clear();
});
