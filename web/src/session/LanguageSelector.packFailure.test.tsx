import { describe, it, expect, vi } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { MeContext } from "./SessionContext";
import * as api from "../api/cluckwork";
import i18n from "../i18n";
import { LanguageSelector } from "./LanguageSelector";

// #1149 — the tl chunk cannot load in this file, like a device that was offline
// when it first asked for it. i18next's real failed-load state is in play.
vi.mock("../i18n/tl", () => {
  throw new Error("Failed to fetch dynamically imported module");
});
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
  it("keeps the current language offline, then saves and reloads once back online", async () => {
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
    expect(globalThis.location.reload).not.toHaveBeenCalled();

    online.mockReturnValue(true);
    fireEvent.change(select, { target: { value: "tl" } });
    await waitFor(() => expect(globalThis.location.reload).toHaveBeenCalledTimes(1));
    expect(vi.mocked(api.putMeLanguage)).toHaveBeenCalledWith("tl");
    expect(changeSpy).not.toHaveBeenCalled();
    online.mockRestore();
  });
});
