import { describe, it, expect, vi } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { MeContext } from "./SessionContext";
import * as api from "../api/cluckwork";
import i18n, { SUPPORTED_LANGUAGES } from "../i18n";

// Production installs three packs today (en/es/tl, #182) — no override needed
// here, this exercises the REAL SUPPORTED_LANGUAGES so the selector renders
// and its onChange runs. (English-only stays hidden — see LanguageSelector.test.tsx.)
vi.mock("../api/cluckwork", async (io) => ({
  ...(await io<typeof import("../api/cluckwork")>()),
  putMeLanguage: vi.fn().mockResolvedValue(undefined),
}));

describe("LanguageSelector with all installed languages", () => {
  it("shows every installed language and persists + switches on change", async () => {
    const { LanguageSelector } = await import("./LanguageSelector");
    const changeSpy = vi.spyOn(i18n, "changeLanguage");
    render(
      <MeContext.Provider value={{ id: "u1", email: "a@b.co", name: null, role: "Admin", language: "en" }}>
        <LanguageSelector />
      </MeContext.Provider>,
    );
    const select = screen.getByRole("combobox") as HTMLSelectElement;
    expect(select).toBeInTheDocument();
    expect(SUPPORTED_LANGUAGES).toEqual(["en", "es", "tl"]);
    const optionLabels = Array.from(select.options).map((o) => o.textContent);
    expect(optionLabels).toEqual(["English", "Español", "Tagalog"]);

    fireEvent.change(select, { target: { value: "es" } });
    expect(vi.mocked(api.putMeLanguage)).toHaveBeenCalledWith("es");
    expect(changeSpy).toHaveBeenCalledWith("es");
    // Regression guard: the select must reflect the live i18n language, not the
    // stale `me` object (MeContext is never updated on a language switch).
    expect(select).toHaveValue("es");

    await i18n.changeLanguage("en"); // reset for subsequent tests
  });
});
