import { describe, it, expect, vi } from "vitest";
import { screen, fireEvent } from "@testing-library/react";
import { AccountPage } from "./AccountPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { putMeLanguage } from "../api/cluckwork";
import { LAZY_PACKS } from "../i18n";

vi.mock("../api/cluckwork", async (io) => ({
  ...(await io<typeof import("../api/cluckwork")>()),
  putMeLanguage: vi.fn().mockResolvedValue(undefined),
}));

// #1149 review round 2 — a pack that fails online must not reload the page by
// itself: the Change password form beside the picker may hold a draft.
describe("AccountPage when a language pack fails online (#1149)", () => {
  it("keeps a typed password draft until the user chooses Reload", async () => {
    vi.spyOn(LAZY_PACKS, "es").mockRejectedValue(new Error("Failed to fetch dynamically imported module"));
    vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
    vi.stubGlobal("location", { reload: vi.fn() });
    renderWithProviders(<AccountPage />, { token: { sub: "u1", role: "Worker" } });

    const draft = `Aa1!${crypto.randomUUID()}`;
    for (const label of [/Current password/, /New password/, /Confirm new password/])
      fireEvent.change(screen.getByLabelText(label), { target: { value: draft } });
    fireEvent.change(screen.getByRole("combobox", { name: "Language" }), { target: { value: "es" } });

    await screen.findByRole("button", { name: "Reload" });
    expect(vi.mocked(putMeLanguage)).toHaveBeenCalledWith("es");
    expect(globalThis.location.reload).not.toHaveBeenCalled();
    for (const label of [/Current password/, /New password/, /Confirm new password/])
      expect(screen.getByLabelText(label)).toHaveValue(draft);
  });
});
