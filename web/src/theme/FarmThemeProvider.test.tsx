import { afterEach, describe, expect, it } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { useTheme } from "@mui/material/styles";
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
});
