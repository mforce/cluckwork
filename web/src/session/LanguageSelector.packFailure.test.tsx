import { describe, it, expect, vi } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { MeContext } from "./SessionContext";
import * as api from "../api/cluckwork";
import i18n, { LAZY_PACKS } from "../i18n";
import { LanguageSelector } from "./LanguageSelector";

vi.mock("../api/cluckwork", async (io) => ({
  ...(await io<typeof import("../api/cluckwork")>()),
  putMeLanguage: vi.fn().mockResolvedValue(undefined),
}));

function renderSelector() {
  render(
    <MeContext.Provider
      value={{ id: "u1", email: "a@b.co", name: null, role: "Admin", language: "en", preferredStepperUnit: null }}>
      <LanguageSelector />
    </MeContext.Provider>,
  );
  return screen.getByRole("combobox");
}

describe("LanguageSelector when a pack cannot load (#1149)", () => {
  it("keeps the current language offline, then saves and offers a reload once back online", async () => {
    // The tl chunk cannot load, like a device that was offline when it first
    // asked for it; i18next's real failed-load state is in play.
    vi.spyOn(LAZY_PACKS, "tl").mockRejectedValue(new Error("Failed to fetch dynamically imported module"));
    const online = vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
    vi.stubGlobal("location", { reload: vi.fn() });
    const changeSpy = vi.spyOn(i18n, "changeLanguage").mockClear();
    const select = renderSelector();

    fireEvent.change(select, { target: { value: "tl" } });
    expect(await screen.findByRole("status")).toHaveTextContent(
      "Tagalog is not on this device yet. Connect to the internet once to switch to it.");
    expect(select).toHaveValue("en");
    expect(changeSpy).not.toHaveBeenCalled();
    expect(vi.mocked(api.putMeLanguage)).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: "Reload" })).toBeNull();

    online.mockReturnValue(true);
    fireEvent.change(select, { target: { value: "tl" } });
    const reload = await screen.findByRole("button", { name: "Reload" });
    expect(vi.mocked(api.putMeLanguage)).toHaveBeenCalledWith("tl");
    expect(screen.getByRole("status")).toHaveTextContent(
      "Tagalog is saved for you, but this page could not load it. Select Reload to switch to it.");
    expect(globalThis.location.reload).not.toHaveBeenCalled();
    expect(changeSpy).not.toHaveBeenCalled();

    fireEvent.click(reload);
    expect(globalThis.location.reload).toHaveBeenCalledTimes(1);
    online.mockRestore();
  });

  it("keeps the page and offers Try again when saving the choice fails", async () => {
    vi.spyOn(LAZY_PACKS, "tl").mockRejectedValue(new Error("Failed to fetch dynamically imported module"));
    const online = vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
    vi.stubGlobal("location", { reload: vi.fn() });
    vi.mocked(api.putMeLanguage).mockClear().mockRejectedValueOnce(new Error("500"));
    const select = renderSelector();

    fireEvent.change(select, { target: { value: "tl" } });
    const tryAgain = await screen.findByRole("button", { name: "Try again" });
    expect(screen.getByRole("status")).toHaveTextContent(
      "Tagalog could not be saved. Check your connection, then select Try again.");
    expect(screen.queryByRole("button", { name: "Reload" })).toBeNull();
    expect(globalThis.location.reload).not.toHaveBeenCalled();

    fireEvent.click(tryAgain);
    await screen.findByRole("button", { name: "Reload" });
    expect(vi.mocked(api.putMeLanguage)).toHaveBeenCalledTimes(2);
    expect(globalThis.location.reload).not.toHaveBeenCalled();
    online.mockRestore();
  });

  it("shows the language on screen after the bootstrap's switch fails to load", async () => {
    vi.spyOn(LAZY_PACKS, "tl").mockRejectedValue(new Error("Failed to fetch dynamically imported module"));
    await i18n.changeLanguage("tl");
    expect(i18n.language).toBe("tl");
    expect(renderSelector()).toHaveValue("en");
  });
});
