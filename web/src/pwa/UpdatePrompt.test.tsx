import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, act, fireEvent } from "@testing-library/react";
import { UpdatePrompt } from "./UpdatePrompt";
import { registerServiceWorker } from "./registerServiceWorker";
import i18n from "../i18n";

vi.mock("./registerServiceWorker", () => ({ registerServiceWorker: vi.fn() }));
const mockRegister = vi.mocked(registerServiceWorker);

/** Renders, then hands back the update callback the component supplied. */
async function renderAndCapture() {
  let announce: ((activate: () => Promise<void>) => void) | undefined;
  mockRegister.mockImplementation(async (onUpdate) => {
    announce = onUpdate;
    return null;
  });
  await act(async () => {
    render(<UpdatePrompt />);
  });
  return {
    announce: (activate: () => Promise<void>) => act(() => { announce?.(activate); }),
  };
}

// The VISIBLE banner specifically. Since #485 the same sentence also lives in
// an always-mounted sr-only live region — the accessible copy, and the one
// that actually gets announced — so an unscoped text query matches two nodes.
const banner = () =>
  screen.queryByText(/new version of Cluckwork is ready/i, {
    selector: ".update-banner-text",
  });

beforeEach(() => vi.resetAllMocks());

describe("UpdatePrompt (#142)", () => {
  it("renders nothing until an update is actually waiting", async () => {
    await renderAndCapture();
    expect(banner()).not.toBeInTheDocument();
  });

  it("stays invisible where service workers are unsupported", async () => {
    // Off a secure context registerServiceWorker resolves null and never
    // announces — the component must add no UI at all.
    mockRegister.mockResolvedValue(null);
    await act(async () => { render(<UpdatePrompt />); });
    expect(banner()).not.toBeInTheDocument();
  });

  it("shows the banner once an update is announced", async () => {
    const { announce } = await renderAndCapture();
    announce(vi.fn().mockResolvedValue(undefined));

    expect(banner()).toBeInTheDocument();
    // Announced politely so a screen reader doesn't steal focus mid-entry —
    // from the sr-only live region, which since #485 is where the
    // announcement lives (the visible banner is inert whenever a dialog is
    // open). Role-less by design; see UpdatePrompt.tsx.
    const live = document.querySelector(".sr-only[aria-live='polite']");
    expect(live).toHaveTextContent(/new version of Cluckwork is ready/i);
    expect(screen.getByRole("button", { name: "Reload" })).toBeInTheDocument();
  });

  it("activates the waiting worker when Reload is pressed", async () => {
    const activate = vi.fn().mockResolvedValue(undefined);
    const { announce } = await renderAndCapture();
    announce(activate);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Reload" }));
    });

    expect(activate).toHaveBeenCalledTimes(1);
  });

  it("does not activate twice when Reload is double-tapped", async () => {
    // Activation ends in a page reload; firing it twice would be a second
    // SKIP_WAITING against a worker that is already taking over.
    let release: () => void = () => {};
    const activate = vi.fn(() => new Promise<void>((r) => { release = r; }));
    const { announce } = await renderAndCapture();
    announce(activate);

    const button = screen.getByRole("button", { name: "Reload" });
    await act(async () => { fireEvent.click(button); });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: /Reloading/ })); });

    expect(activate).toHaveBeenCalledTimes(1);
    await act(async () => { release(); });
  });

  it("re-enables Reload if activation fails, instead of hanging on a dead spinner", async () => {
    const activate = vi.fn().mockRejectedValue(new Error("worker vanished"));
    const { announce } = await renderAndCapture();
    announce(activate);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Reload" }));
    });

    expect(screen.getByRole("button", { name: "Reload" })).toBeEnabled();
    expect(banner()).toBeInTheDocument();
  });

  it("Later dismisses the banner without activating", async () => {
    const activate = vi.fn();
    const { announce } = await renderAndCapture();
    announce(activate);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Later" }));
    });

    expect(banner()).not.toBeInTheDocument();
    expect(activate).not.toHaveBeenCalled();
  });

  it("a NEWER update re-shows the banner after an earlier one was dismissed", async () => {
    const { announce } = await renderAndCapture();
    announce(vi.fn());
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Later" }));
    });
    expect(banner()).not.toBeInTheDocument();

    announce(vi.fn()); // a second deploy lands
    expect(banner()).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 9, batch B1)
// ---------------------------------------------------------------------------

// `pwa` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts) — src/pwa is also outside the i18n:scan default
// path, so this externalization won't move the scan count either. Under ANY
// UI language the rendered text falls back to this exact English string, same
// as a still-hardcoded literal would render — asserting it, even under a
// non-English locale, would prove nothing (CONTRIBUTING-i18n.md's fallback
// trap). Swap the catalog value at runtime instead, the same i18n.addResource
// technique the other Task 8/9 wiring tests use, so each marker only renders
// if UpdatePrompt actually reads the catalog.
describe("UpdatePrompt i18n wiring (#182, Task 9)", () => {
  async function withOverride(key: string, value: string, run: () => Promise<void>) {
    const original = i18n.getResource("en", "pwa", key) as string;
    i18n.addResource("en", "pwa", key, value);
    try {
      await run();
    } finally {
      i18n.addResource("en", "pwa", key, original);
    }
  }

  it("reads the update-available banner text from the catalog, not a hardcoded literal", async () => {
    await withOverride("updateAvailable", "UPDATE-AVAILABLE-MARKER", async () => {
      const { announce } = await renderAndCapture();
      announce(vi.fn().mockResolvedValue(undefined));
      // Both copies of the sentence read the catalog: the visible one and the
      // sr-only announcer that speaks it (#485).
      expect(screen.getAllByText("UPDATE-AVAILABLE-MARKER")).toHaveLength(2);
      expect(banner()).not.toBeInTheDocument();
    });
  });

  it("reads the Reload button's label from the catalog", async () => {
    await withOverride("reload", "RELOAD-MARKER", async () => {
      const { announce } = await renderAndCapture();
      announce(vi.fn().mockResolvedValue(undefined));
      expect(screen.getByRole("button", { name: "RELOAD-MARKER" })).toBeInTheDocument();
    });
  });

  it("reads the busy Reloading label from the catalog", async () => {
    await withOverride("reloading", "RELOADING-MARKER", async () => {
      let release: () => void = () => {};
      const activate = vi.fn(() => new Promise<void>((r) => { release = r; }));
      const { announce } = await renderAndCapture();
      announce(activate);

      const button = screen.getByRole("button", { name: "Reload" });
      await act(async () => { fireEvent.click(button); });
      expect(screen.getByRole("button", { name: "RELOADING-MARKER" })).toBeInTheDocument();
      await act(async () => { release(); });
    });
  });

  it("reads the Later button's label from the catalog", async () => {
    await withOverride("later", "LATER-MARKER", async () => {
      const { announce } = await renderAndCapture();
      announce(vi.fn());
      expect(screen.getByRole("button", { name: "LATER-MARKER" })).toBeInTheDocument();
    });
  });
});
