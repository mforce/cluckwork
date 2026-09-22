import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, act, fireEvent } from "@testing-library/react";
import { UpdatePrompt } from "./UpdatePrompt";
import { registerServiceWorker } from "./registerServiceWorker";
import { resetUpdateStoreForTests } from "./updateStore";
import { useCachedBannerUrl } from "../lib/bannerCache";
import i18n from "../i18n";

vi.mock("./registerServiceWorker", () => ({ registerServiceWorker: vi.fn() }));
const mockRegister = vi.mocked(registerServiceWorker);

vi.mock("../lib/bannerCache", () => ({ useCachedBannerUrl: vi.fn() }));
const mockBannerUrl = vi.mocked(useCachedBannerUrl);

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

const overlay = () => screen.queryByText(/new version of Cluckwork is ready/i);

beforeEach(() => {
  vi.resetAllMocks();
  resetUpdateStoreForTests();
  mockBannerUrl.mockReturnValue(null);
  // test/setup.ts stubs global fetch to a blanket 401; fetchAvailableVersion
  // treats that as "no available version" (never guesses), which is exactly
  // what most of these tests want as their default.
});

describe("UpdatePrompt overlay (#936)", () => {
  it("renders nothing until an update is actually waiting", async () => {
    await renderAndCapture();
    expect(overlay()).not.toBeInTheDocument();
  });

  it("stays invisible where service workers are unsupported", async () => {
    // Off a secure context registerServiceWorker resolves null and never
    // announces — the component must add no UI at all.
    mockRegister.mockResolvedValue(null);
    await act(async () => { render(<UpdatePrompt />); });
    expect(overlay()).not.toBeInTheDocument();
  });

  it("shows the overlay, as a status region, once an update is announced", async () => {
    const { announce } = await renderAndCapture();
    announce(vi.fn().mockResolvedValue(undefined));

    expect(overlay()).toBeInTheDocument();
    // Announced politely so a screen reader doesn't steal focus mid-entry —
    // not a real modal (see UpdatePrompt.tsx: nothing sets the rest of the
    // page inert), so this deliberately is NOT role="dialog"/aria-modal.
    expect(screen.getByRole("status")).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Reload" })).toBeInTheDocument();
  });

  it("renders no banner image when none is cached for this device", async () => {
    const { announce } = await renderAndCapture();
    announce(vi.fn().mockResolvedValue(undefined));
    expect(document.querySelector(".update-overlay-banner")).toBeNull();
  });

  it("reuses the device-cached farm banner when one is available", async () => {
    mockBannerUrl.mockReturnValue("blob:farm-banner");
    const { announce } = await renderAndCapture();
    announce(vi.fn().mockResolvedValue(undefined));
    const image = document.querySelector(".update-overlay-banner") as HTMLImageElement | null;
    expect(image).not.toBeNull();
    expect(image?.src).toContain("blob:farm-banner");
    // Decorative: the substantive content is the status text, not the image.
    expect(image?.alt).toBe("");
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
    expect(overlay()).toBeInTheDocument();
  });

  it("Later dismisses the overlay without activating", async () => {
    const activate = vi.fn();
    const { announce } = await renderAndCapture();
    announce(activate);

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Later" }));
    });

    expect(overlay()).not.toBeInTheDocument();
    expect(activate).not.toHaveBeenCalled();
  });

  it("a NEWER update re-shows the overlay after an earlier one was dismissed", async () => {
    const { announce } = await renderAndCapture();
    announce(vi.fn());
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Later" }));
    });
    expect(overlay()).not.toBeInTheDocument();

    announce(vi.fn()); // a second deploy lands
    expect(overlay()).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// Version handshake (#936)
// ---------------------------------------------------------------------------

// The fetch/validation logic (fetchAvailableVersion) and the "is this worth
// showing" decision (describeVersionChange) are unit-tested directly in
// appVersion.test.ts with explicit strings — VITE_APP_VERSION is read ONCE
// at module scope (matching AppLayout.tsx's identical, identically-untestable
// pattern) and is unset in every test build, so CURRENT_VERSION can never be
// stubbed to a real value here. What IS testable at this level is the
// integration: a successful handshake still correctly renders NOTHING when
// the current build's own version is unknown, exactly like AppLayout's own
// "#458 — dev/test builds" test.
describe("UpdatePrompt version handshake wiring (#936)", () => {
  it("never shows a version line when this build's own version is unset, even if the handshake succeeds", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ version: "9.9.9" }), { status: 200 }),
    ));
    const { announce } = await renderAndCapture();
    await act(async () => { announce(vi.fn()); });
    await act(async () => {}); // flush the fetch microtask queued by the effect

    expect(document.querySelector(".update-overlay-version")).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 9, batch B1)
// ---------------------------------------------------------------------------

// `pwa` under ANY UI language falls back to English unless a test swaps the
// catalog value at runtime (see translations-status.ts) — asserting English
// proves nothing about whether the component reads the catalog at all.
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

  it("reads the update-available headline from the catalog, not a hardcoded literal", async () => {
    await withOverride("updateAvailable", "UPDATE-AVAILABLE-MARKER", async () => {
      const { announce } = await renderAndCapture();
      announce(vi.fn().mockResolvedValue(undefined));
      expect(screen.getByText("UPDATE-AVAILABLE-MARKER")).toBeInTheDocument();
      expect(overlay()).not.toBeInTheDocument();
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
