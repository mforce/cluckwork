import { describe, it, expect, vi } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { MeContext } from "./SessionContext";
import * as api from "../api/cluckwork";
import i18n from "../i18n";

// Install a second language for THIS file only, so the selector renders and its
// onChange runs. (English-only production stays hidden — the other test file.)
vi.mock("../i18n", async (io) => {
  const actual = await io<typeof import("../i18n")>();
  return { ...actual, SUPPORTED_LANGUAGES: ["en", "es"] };
});
vi.mock("../api/cluckwork", async (io) => ({
  ...(await io<typeof import("../api/cluckwork")>()),
  putMeLanguage: vi.fn().mockResolvedValue(undefined),
}));

describe("LanguageSelector with a second language installed", () => {
  it("shows the options and persists + switches on change", async () => {
    const { LanguageSelector } = await import("./LanguageSelector");
    const changeSpy = vi.spyOn(i18n, "changeLanguage");
    render(
      <MeContext.Provider value={{ id: "u1", email: "a@b.co", name: null, role: "Admin", language: "en" }}>
        <LanguageSelector />
      </MeContext.Provider>,
    );
    const select = screen.getByRole("combobox");
    expect(select).toBeInTheDocument();
    fireEvent.change(select, { target: { value: "es" } });
    expect(vi.mocked(api.putMeLanguage)).toHaveBeenCalledWith("es");
    expect(changeSpy).toHaveBeenCalledWith("es");
  });
});
