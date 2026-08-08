import { describe, it, expect, vi } from "vitest";
import { render, screen, fireEvent, act } from "@testing-library/react";
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
      <MeContext.Provider
        value={{ id: "u1", email: "a@b.co", name: null, role: "Admin", language: "en", preferredStepperUnit: null }}>
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

    // Wrapped in act: the persist's settle now clears the component's pending
    // state (#236), and this flush would otherwise land outside act.
    await act(async () => { await i18n.changeLanguage("en"); }); // reset for subsequent tests
  });

  it("disables the select while the persist is in flight, then re-enables (#236)", async () => {
    const { LanguageSelector } = await import("./LanguageSelector");
    // Held promise: the fire-and-forget PUT gains a component-local guard —
    // the select must be inert exactly while the request is open.
    let resolvePut!: () => void;
    vi.mocked(api.putMeLanguage).mockReturnValue(new Promise<void>((r) => (resolvePut = r)));
    render(
      <MeContext.Provider
        value={{ id: "u1", email: "a@b.co", name: null, role: "Admin", language: "en", preferredStepperUnit: null }}>
        <LanguageSelector />
      </MeContext.Provider>,
    );

    const select = screen.getByRole("combobox");
    fireEvent.change(select, { target: { value: "tl" } });
    expect(select).toBeDisabled(); // in flight — a second switch cannot race the first

    await act(async () => resolvePut());
    expect(select).toBeEnabled();

    await act(async () => { await i18n.changeLanguage("en"); }); // reset for subsequent tests
  });
});
