import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen } from "@testing-library/react";
import { FarmBrand } from "./FarmBrand";
import { FarmContext } from "../farm/FarmContext";
import type { FarmState } from "../farm/FarmContext";
import { getFarmLogo } from "../api/cluckwork";
import { account } from "../test/fixtures";

vi.mock("../api/cluckwork", async () => {
  const actual = await vi.importActual<typeof import("../api/cluckwork")>("../api/cluckwork");
  return { ...actual, getFarmLogo: vi.fn() };
});

const mockGetFarmLogo = vi.mocked(getFarmLogo);

// The provider is stubbed rather than driven through /account: this component's
// job is what it renders for a given farm, and FarmContext.test covers how the
// farm gets there.
function renderBrand(state: Partial<FarmState>) {
  const value: FarmState = { farm: null, refresh: async () => {}, ...state };
  return render(<FarmContext.Provider value={value}><FarmBrand /></FarmContext.Provider>);
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.stubGlobal("URL", {
    ...URL,
    createObjectURL: vi.fn(() => "blob:test/logo"),
    revokeObjectURL: vi.fn(),
  });
});

describe("FarmBrand", () => {
  it("shows the app's own name before the farm has loaded", () => {
    renderBrand({ farm: null });
    expect(screen.getByText("Cluckwork")).toBeInTheDocument();
    expect(screen.queryByRole("img")).not.toBeInTheDocument();
    expect(mockGetFarmLogo).not.toHaveBeenCalled();
  });

  it("names the farm once it is known — the SPA showed it nowhere before (#123)", () => {
    renderBrand({ farm: account({ name: "Hen House" }) });
    expect(screen.getByText("Hen House")).toBeInTheDocument();
    expect(screen.queryByText("Cluckwork")).not.toBeInTheDocument();
  });

  it("falls back to the app mark when the farm has no logo, and never asks for one", () => {
    renderBrand({ farm: account({ name: "Hen House", logoContentHash: null }) });
    // The egg mark is decorative (aria-hidden), so nothing exposes an img role.
    expect(screen.queryByRole("img")).not.toBeInTheDocument();
    expect(mockGetFarmLogo).not.toHaveBeenCalled();
  });

  it("renders the farm's logo when one is set", async () => {
    mockGetFarmLogo.mockResolvedValue({ blob: new Blob(["png"]), filename: null });
    renderBrand({ farm: account({ name: "Hen House", logoContentHash: "deadbeef" }) });

    const img = await screen.findByRole("presentation");
    expect(img).toHaveAttribute("src", "blob:test/logo");
    // Empty alt: the farm name is right beside it, so the image is decoration
    // and must not make a screen reader say the farm twice.
    expect(img).toHaveAttribute("alt", "");
    expect(screen.getByText("Hen House")).toBeInTheDocument();
  });

  it("keeps the name and the app mark when the logo will not load", async () => {
    mockGetFarmLogo.mockRejectedValue(new Error("410 gone"));
    renderBrand({ farm: account({ name: "Hen House", logoContentHash: "deadbeef" }) });

    // A broken logo must not cost the farm its name or blank the sidebar.
    expect(await screen.findByText("Hen House")).toBeInTheDocument();
    expect(screen.queryByRole("presentation")).not.toBeInTheDocument();
  });
});
