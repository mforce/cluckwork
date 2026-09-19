import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { BrandSplash } from "./BrandSplash";
import { getFarmBanner } from "../api/cluckwork";
import { bindAccount, bindFarm } from "../auth/tokenStore";
import { readCachedBannerBlob } from "../lib/bannerCache";

vi.mock("../api/cluckwork", async () => {
  const actual = await vi.importActual<typeof import("../api/cluckwork")>("../api/cluckwork");
  return { ...actual, getFarmBanner: vi.fn() };
});

const mockGetFarmBanner = vi.mocked(getFarmBanner);

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  vi.stubGlobal("URL", {
    ...URL,
    createObjectURL: vi.fn(() => "blob:test/banner"),
    revokeObjectURL: vi.fn(),
  });
});

describe("BrandSplash", () => {
  it("shows the banner once fetched, with the farm name in its alt text", async () => {
    mockGetFarmBanner.mockResolvedValue({ blob: new Blob(["png"]), filename: null });
    render(<BrandSplash farmName="Hen House" bannerContentHash="abc" onDismiss={vi.fn()} />);

    const img = await screen.findByAltText("Hen House banner");
    expect(img).toHaveAttribute("src", "blob:test/banner");
  });

  it("focuses Continue on mount, so a keyboard user is not dropped on body", async () => {
    mockGetFarmBanner.mockReturnValue(new Promise(() => {}));
    render(<BrandSplash farmName="Hen House" bannerContentHash="abc" onDismiss={vi.fn()} />);

    expect(screen.getByRole("button", { name: "Continue" })).toHaveFocus();
  });

  it("calls onDismiss when Continue is clicked, even before the banner has loaded", () => {
    mockGetFarmBanner.mockReturnValue(new Promise(() => {}));
    const onDismiss = vi.fn();
    render(<BrandSplash farmName="Hen House" bannerContentHash="abc" onDismiss={onDismiss} />);

    screen.getByRole("button", { name: "Continue" }).click();

    expect(onDismiss).toHaveBeenCalledTimes(1);
  });

  it("auto-dismisses and renders nothing when the banner fails to load", async () => {
    mockGetFarmBanner.mockRejectedValue(new Error("404"));
    const onDismiss = vi.fn();
    const { container } = render(
      <BrandSplash farmName="Hen House" bannerContentHash="abc" onDismiss={onDismiss} />);

    await waitFor(() => expect(onDismiss).toHaveBeenCalledTimes(1));
    expect(container).toBeEmptyDOMElement();
  });

  it("is announced as a modal dialog labelled with the farm name", () => {
    mockGetFarmBanner.mockReturnValue(new Promise(() => {}));
    render(<BrandSplash farmName="Hen House" bannerContentHash="abc" onDismiss={vi.fn()} />);

    expect(screen.getByRole("dialog", { name: "Hen House" })).toBeInTheDocument();
  });

  // #833 — owner decision, 2026-09-19: the splash is where the banner is
  // fetched, so it is where the bytes are cached for Login's own pre-auth
  // display. Real bindAccount/bindFarm (not mocked), same as brand.test.ts,
  // because cacheBannerBytes checks the live binding.
  it("caches the fetched banner under the bound farm, once it loads", async () => {
    bindAccount("acct-A");
    bindFarm("sunny-acres");
    mockGetFarmBanner.mockResolvedValue({ blob: new Blob(["png-bytes"]), filename: null });
    render(<BrandSplash farmName="Hen House" bannerContentHash="abc" onDismiss={vi.fn()} />);

    await screen.findByAltText("Hen House banner");
    await waitFor(async () => expect(await readCachedBannerBlob("sunny-acres")).not.toBeNull());
  });

  it("does not cache anything while the fetch is still pending", async () => {
    bindAccount("acct-A");
    bindFarm("sunny-acres");
    mockGetFarmBanner.mockReturnValue(new Promise(() => {}));
    render(<BrandSplash farmName="Hen House" bannerContentHash="abc" onDismiss={vi.fn()} />);

    expect(await readCachedBannerBlob("sunny-acres")).toBeNull();
  });

  it("caches nothing on an unbound tab (a fresh tab restored from the refresh cookie)", async () => {
    // Deliberately no bindAccount/bindFarm — mirrors applyBrand's own
    // unbound-tab contract in brand.test.ts.
    mockGetFarmBanner.mockResolvedValue({ blob: new Blob(["png-bytes"]), filename: null });
    render(<BrandSplash farmName="Hen House" bannerContentHash="abc" onDismiss={vi.fn()} />);

    await screen.findByAltText("Hen House banner");
    expect(await readCachedBannerBlob("sunny-acres")).toBeNull();
  });
});
