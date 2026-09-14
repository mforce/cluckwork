import { afterEach, describe, expect, it } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { useTheme } from "@mui/material/styles";
import Chip from "@mui/material/Chip";
import { FarmThemeProvider } from "./FarmThemeProvider";

function Probe() {
  const theme = useTheme();
  return <output>{theme.palette.mode}</output>;
}

afterEach(() => {
  delete document.documentElement.dataset.theme;
  delete document.documentElement.dataset.brand;
});

describe("FarmThemeProvider (#674)", () => {
  it("hands MUI the document's current mode on mount", () => {
    document.documentElement.dataset.theme = "dark";
    render(<FarmThemeProvider><Probe /></FarmThemeProvider>);
    expect(screen.getByRole("status")).toHaveTextContent("dark");
  });

  it("follows a data-theme change made outside React", async () => {
    render(<FarmThemeProvider><Probe /></FarmThemeProvider>);
    expect(screen.getByRole("status")).toHaveTextContent("light");
    document.documentElement.dataset.theme = "dark";
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("dark"));
    document.documentElement.dataset.theme = "light";
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("light"));
  });

  // #873 — this file carries NO csp-nonce meta, which is the Vite dev server's
  // document. The cache must then carry no nonce at all rather than an empty or
  // literal one: `nonce=""` is a value the browser compares against the policy
  // and rejects, so a fallback here would break dev to look tidy in production.
  // The meta-present direction is FarmThemeProvider.nonce.test.tsx.
  it("leaves the nonce off when the document carries no meta", () => {
    expect(document.querySelector('meta[name="csp-nonce"]')).toBeNull();

    render(<FarmThemeProvider><Chip label="probe" color="primary" /></FarmThemeProvider>);

    const styles = [...document.querySelectorAll<HTMLStyleElement>("style[data-emotion]")];
    expect(styles.length).toBeGreaterThan(0);
    for (const style of styles) expect(style.hasAttribute("nonce")).toBe(false);
  });
});
