import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen } from "@testing-library/react";
import { useBannerObjectUrl } from "./useLogoObjectUrl";
import { getFarmBanner, getFarmLogo } from "../api/cluckwork";

// useBannerObjectUrl is a thin wrapper around the same generic hook
// useLogoObjectUrl.test.tsx already covers exhaustively (loading/failed/
// cancellation/revocation) — this only proves the wiring: that it fetches
// through getFarmBanner, and never through getFarmLogo.
vi.mock("../api/cluckwork", async () => {
  const actual = await vi.importActual<typeof import("../api/cluckwork")>("../api/cluckwork");
  return { ...actual, getFarmBanner: vi.fn(), getFarmLogo: vi.fn() };
});

const mockGetFarmBanner = vi.mocked(getFarmBanner);
const mockGetFarmLogo = vi.mocked(getFarmLogo);

function Probe({ hash }: { hash: string | null }) {
  const { url } = useBannerObjectUrl(hash);
  return <p data-testid="url">{url ?? "none"}</p>;
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.stubGlobal("URL", {
    ...URL,
    createObjectURL: vi.fn(() => "blob:test/0"),
    revokeObjectURL: vi.fn(),
  });
});

describe("useBannerObjectUrl", () => {
  it("fetches through getFarmBanner, never getFarmLogo", async () => {
    mockGetFarmBanner.mockResolvedValue({ blob: new Blob(["png-bytes"]), filename: null });

    render(<Probe hash="abc" />);

    expect(await screen.findByText("blob:test/0")).toBeInTheDocument();
    expect(mockGetFarmBanner).toHaveBeenCalledTimes(1);
    expect(mockGetFarmLogo).not.toHaveBeenCalled();
  });

  it("does not call the endpoint when the farm has no banner", () => {
    render(<Probe hash={null} />);
    expect(mockGetFarmBanner).not.toHaveBeenCalled();
    expect(screen.getByTestId("url")).toHaveTextContent("none");
  });
});
