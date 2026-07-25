import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, act, fireEvent } from "@testing-library/react";
import { UpdatePrompt } from "./UpdatePrompt";
import { registerServiceWorker } from "./registerServiceWorker";

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

const banner = () => screen.queryByText(/new version of Cluckwork is ready/i);

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
    // Announced politely so a screen reader doesn't steal focus mid-entry.
    expect(screen.getByRole("status")).toBeInTheDocument();
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
