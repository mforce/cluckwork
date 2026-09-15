// #873 — the nonce half of FarmThemeProvider, in its own file because the
// module reads the meta ONCE at import time. `vi.hoisted` is what runs before
// the import below, so this file proves the real wiring rather than a factory
// the app does not call. The no-meta direction lives in FarmThemeProvider.test.tsx,
// which is a separate file for the same reason: two module-load states cannot
// share one module registry.
import { describe, expect, it, vi } from "vitest";
import { render } from "@testing-library/react";
import Chip from "@mui/material/Chip";

const NONCE = "Y3NwLW5vbmNlLXRlc3QtdmFs";

vi.hoisted(() => {
  const meta = document.createElement("meta");
  meta.name = "csp-nonce";
  meta.content = "Y3NwLW5vbmNlLXRlc3QtdmFs";
  document.head.appendChild(meta);
});

import { FarmThemeProvider } from "./FarmThemeProvider";

describe("FarmThemeProvider under a CSP nonce (#873)", () => {
  it("stamps the page's nonce on every style element Emotion injects", () => {
    render(<FarmThemeProvider><Chip label="probe" color="primary" /></FarmThemeProvider>);

    // MUI styles through Emotion, and these are the elements the browser drops
    // under `style-src 'self'` when they carry no nonce — which is the whole
    // defect #873 fixes. Asserting the tags EXIST first keeps the assertion from
    // passing vacuously on a render that injected nothing.
    const styles = [...document.querySelectorAll<HTMLStyleElement>("style[data-emotion]")];
    expect(styles.length).toBeGreaterThan(0);
    for (const style of styles) expect(style.getAttribute("nonce")).toBe(NONCE);
  });
});
