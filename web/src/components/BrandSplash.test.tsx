import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { BrandSplash } from "./BrandSplash";
import { getFarmBanner } from "../api/cluckwork";

vi.mock("../api/cluckwork", async () => {
  const actual = await vi.importActual<typeof import("../api/cluckwork")>("../api/cluckwork");
  return { ...actual, getFarmBanner: vi.fn() };
});

const mockGetFarmBanner = vi.mocked(getFarmBanner);

beforeEach(() => {
  vi.clearAllMocks();
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
});
