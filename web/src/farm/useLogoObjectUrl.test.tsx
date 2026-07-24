import { describe, it, expect, vi, beforeEach } from "vitest";
import { act, render, screen, waitFor } from "@testing-library/react";
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
  const { url } = useLogoObjectUrl(hash);
  return <p data-testid="url">{url ?? "none"}</p>;
}

function Status({ hash }: { hash: string | null }) {
  const { url, loading, failed } = useLogoObjectUrl(hash);
  const state = loading ? "loading" : failed ? "failed" : url ?? "none";
  return <p data-testid="status">{state}</p>;
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

  it("ignores a failure that belongs to a hash it has moved on from", async () => {
    // The observable form of the cancellation guard. Unmounting proves nothing
    // — a setState on an unmounted component is a silent no-op in React 19, so
    // the old test passed with the guard deleted (round 2: codex + pi). Here
    // the component is still mounted and mid-flight on a SECOND hash, so a
    // leaked `failed` from the first would be visible.
    let rejectFirst: ((reason: Error) => void) | undefined;
    mockGetFarmLogo.mockReturnValueOnce(new Promise((_, r) => { rejectFirst = r; }));
    const { rerender } = render(<Status hash="abc" />);

    mockGetFarmLogo.mockReturnValueOnce(new Promise(() => {}));
    rerender(<Status hash="def" />);

    await act(async () => { rejectFirst!(new Error("500")); });

    // Still loading "def" — not failed on "abc"'s account.
    expect(screen.getByTestId("status")).toHaveTextContent("loading");
  });

  it("says it is loading from the very first render, not one commit later", () => {
    // No await: the assertion is about the SYNCHRONOUS first render. A
    // `loading` flag set inside the effect is false here, and a caller keyed on
    // it renders "no logo" for a farm that has one — which is both a visible
    // contradiction and, in the settings test, an intermittent failure.
    mockGetFarmLogo.mockReturnValue(new Promise(() => {}));
    render(<Status hash="abc" />);
    expect(screen.getByTestId("status")).toHaveTextContent("loading");
  });

  it("touches no state when the FAILURE arrives after unmount", async () => {
    let reject: ((reason: Error) => void) | undefined;
    mockGetFarmLogo.mockReturnValue(new Promise((_, r) => { reject = r; }));

    const { unmount } = render(<Probe hash="abc" />);
    unmount();
    reject!(new Error("500"));

    // The success path guards against a late response; the failure path has to
    // as well, or an unmounted hook sets `failed` on a component that is gone.
    await waitFor(() => expect(mockGetFarmLogo).toHaveBeenCalled());
    expect(minted).toEqual([]);
    expect(revoked).toEqual([]);
  });

  it("reports loading, then failure, so a caller can tell them apart", async () => {
    let reject: ((reason: Error) => void) | undefined;
    mockGetFarmLogo.mockReturnValue(new Promise((_, r) => { reject = r; }));

    render(<Status hash="abc" />);
    // Three different reasons there is no URL, and the caller must not report
    // "no logo set" for the other two.
    expect(screen.getByTestId("status")).toHaveTextContent("loading");

    await act(async () => { reject!(new Error("500")); });
    expect(screen.getByTestId("status")).toHaveTextContent("failed");
  });

  it("reports neither loading nor failed when there is simply no logo", () => {
    render(<Status hash={null} />);
    expect(screen.getByTestId("status")).toHaveTextContent("none");
  });

  it("stays silent when the fetch fails — the caller renders its own fallback", async () => {
    mockGetFarmLogo.mockRejectedValue(new Error("404"));
    render(<Probe hash="abc" />);
    await waitFor(() => expect(mockGetFarmLogo).toHaveBeenCalled());
    expect(screen.getByTestId("url")).toHaveTextContent("none");
  });
});
