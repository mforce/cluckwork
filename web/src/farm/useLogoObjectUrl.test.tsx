import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { useLogoObjectUrl } from "./useLogoObjectUrl";
import { getFarmLogo } from "../api/cluckwork";

vi.mock("../api/cluckwork", async () => {
  const actual = await vi.importActual<typeof import("../api/cluckwork")>("../api/cluckwork");
  return { ...actual, getFarmLogo: vi.fn() };
});

const mockGetFarmLogo = vi.mocked(getFarmLogo);

// jsdom implements neither half of the object-URL API. Stubs that hand back a
// distinguishable URL per call, and record every revoke, so the test can assert
// the lifetime rather than only the happy path.
let minted: string[] = [];
let revoked: string[] = [];

function Probe({ hash }: { hash: string | null }) {
  const url = useLogoObjectUrl(hash);
  return <p data-testid="url">{url ?? "none"}</p>;
}

const logo = (body = "png-bytes") => ({ blob: new Blob([body]), filename: null });

beforeEach(() => {
  vi.clearAllMocks();
  minted = [];
  revoked = [];
  vi.stubGlobal("URL", {
    ...URL,
    createObjectURL: vi.fn(() => {
      const url = `blob:test/${minted.length}`;
      minted.push(url);
      return url;
    }),
    revokeObjectURL: vi.fn((url: string) => { revoked.push(url); }),
  });
});

describe("useLogoObjectUrl", () => {
  it("does not call the endpoint when the farm has no logo", () => {
    render(<Probe hash={null} />);
    expect(mockGetFarmLogo).not.toHaveBeenCalled();
    expect(screen.getByTestId("url")).toHaveTextContent("none");
  });

  it("fetches the bytes and hands back an object URL", async () => {
    mockGetFarmLogo.mockResolvedValue(logo());
    render(<Probe hash="abc" />);
    expect(await screen.findByText("blob:test/0")).toBeInTheDocument();
    expect(mockGetFarmLogo).toHaveBeenCalledTimes(1);
  });

  it("re-fetches when the hash changes and revokes the URL it replaces", async () => {
    mockGetFarmLogo.mockResolvedValue(logo());
    const { rerender } = render(<Probe hash="abc" />);
    expect(await screen.findByText("blob:test/0")).toBeInTheDocument();

    rerender(<Probe hash="def" />);
    expect(await screen.findByText("blob:test/1")).toBeInTheDocument();
    // The replaced URL is released, and never left on screen pointing at bytes
    // that no longer exist.
    expect(revoked).toEqual(["blob:test/0"]);
  });

  it("does NOT re-fetch when the hash is unchanged across a re-render", async () => {
    mockGetFarmLogo.mockResolvedValue(logo());
    const { rerender } = render(<Probe hash="abc" />);
    expect(await screen.findByText("blob:test/0")).toBeInTheDocument();

    rerender(<Probe hash="abc" />);
    // A settings save that did not touch the logo must not pull a megabyte
    // down again — the hash, not the farm object, is the dependency.
    expect(mockGetFarmLogo).toHaveBeenCalledTimes(1);
  });

  it("revokes on unmount", async () => {
    mockGetFarmLogo.mockResolvedValue(logo());
    const { unmount } = render(<Probe hash="abc" />);
    expect(await screen.findByText("blob:test/0")).toBeInTheDocument();

    unmount();
    expect(revoked).toEqual(["blob:test/0"]);
  });

  it("drops back to no URL when the logo is removed", async () => {
    mockGetFarmLogo.mockResolvedValue(logo());
    const { rerender } = render(<Probe hash="abc" />);
    expect(await screen.findByText("blob:test/0")).toBeInTheDocument();

    rerender(<Probe hash={null} />);
    expect(screen.getByTestId("url")).toHaveTextContent("none");
    expect(revoked).toEqual(["blob:test/0"]);
  });

  it("mints nothing when the response arrives after unmount", async () => {
    let resolve: ((value: ReturnType<typeof logo>) => void) | undefined;
    mockGetFarmLogo.mockReturnValue(new Promise((r) => { resolve = r; }));

    const { unmount } = render(<Probe hash="abc" />);
    unmount();
    resolve!(logo());

    // A URL minted after unmount would never be revoked by anyone.
    await waitFor(() => expect(mockGetFarmLogo).toHaveBeenCalled());
    expect(minted).toEqual([]);
    expect(revoked).toEqual([]);
  });

  it("stays silent when the fetch fails — the caller renders its own fallback", async () => {
    mockGetFarmLogo.mockRejectedValue(new Error("404"));
    render(<Probe hash="abc" />);
    await waitFor(() => expect(mockGetFarmLogo).toHaveBeenCalled());
    expect(screen.getByTestId("url")).toHaveTextContent("none");
  });
});
