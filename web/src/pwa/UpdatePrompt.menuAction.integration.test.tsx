import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, act, fireEvent, within, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { BottomNav } from "../components/BottomNav";
import { navGroups, tabEntries } from "../routes/nav";
import { UpdatePrompt } from "./UpdatePrompt";
import { registerServiceWorker } from "./registerServiceWorker";
import { resetUpdateStoreForTests } from "./updateStore";
import { useCachedBannerUrl } from "../lib/bannerCache";

// #936 — the deferred-update recovery path, exercised the way the real app
// composes it: UpdatePrompt (outside auth/router) and BottomNav's More sheet
// (deep inside AppLayout) share exactly ONE registerServiceWorker call
// through updateStore.ts. AppLayout's own desktop sidebar action reads the
// identical store the same way (see AppLayout.tsx), so this one surface is
// representative of both.

vi.mock("./registerServiceWorker", () => ({ registerServiceWorker: vi.fn() }));
const mockRegister = vi.mocked(registerServiceWorker);

vi.mock("../lib/bannerCache", () => ({ useCachedBannerUrl: vi.fn() }));

beforeEach(() => {
  vi.resetAllMocks();
  resetUpdateStoreForTests();
  vi.mocked(useCachedBannerUrl).mockReturnValue(null);
});

function Harness() {
  const groups = navGroups("Admin", true);
  const tabs = tabEntries(groups);
  return (
    <MemoryRouter initialEntries={["/"]}>
      <UpdatePrompt />
      <BottomNav groups={groups} tabs={tabs} onLogout={vi.fn()} />
    </MemoryRouter>
  );
}

async function renderHarness() {
  let announce: ((activate: () => Promise<void>) => void) | undefined;
  mockRegister.mockImplementation(async (onUpdate) => {
    announce = onUpdate;
    return null;
  });
  await act(async () => { render(<Harness />); });
  return {
    announce: (activate: () => Promise<void>) => act(() => { announce?.(activate); }),
  };
}

const overlay = () => document.querySelector(".update-overlay");
const openMoreSheet = () => fireEvent.click(screen.getByRole("button", { name: "More" }));

describe("Deferred update recovery via the More menu (#936)", () => {
  it("shows no recovery action while no update is waiting", async () => {
    await renderHarness();
    openMoreSheet();
    expect(within(screen.getByRole("dialog")).queryByText("Update available")).not.toBeInTheDocument();
  });

  it("shows the recovery action alongside the overlay once an update is waiting", async () => {
    const { announce } = await renderHarness();
    await announce(vi.fn());

    expect(overlay()).not.toBeNull();
    openMoreSheet();
    const sheet = within(screen.getByRole("dialog"));
    expect(sheet.getByRole("button", { name: "Update available" })).toBeInTheDocument();
    expect(sheet.getByRole("button", { name: "Sign out" })).toBeInTheDocument();
  });

  it("Later hides the overlay but leaves the recovery action available", async () => {
    const { announce } = await renderHarness();
    await announce(vi.fn());

    fireEvent.click(screen.getByRole("button", { name: "Later" }));
    expect(overlay()).toBeNull();

    openMoreSheet();
    expect(within(screen.getByRole("dialog")).getByRole("button", { name: "Update available" })).toBeInTheDocument();
  });

  it("choosing the recovery action closes the sheet and reopens the same overlay", async () => {
    const { announce } = await renderHarness();
    await announce(vi.fn());
    fireEvent.click(screen.getByRole("button", { name: "Later" }));
    expect(overlay()).toBeNull();

    openMoreSheet();
    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Update available" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(overlay()).not.toBeNull();
  });

  it("Reload still activates exactly once after a defer/reopen round trip", async () => {
    const activate = vi.fn().mockResolvedValue(undefined);
    const { announce } = await renderHarness();
    await announce(activate);
    fireEvent.click(screen.getByRole("button", { name: "Later" }));
    openMoreSheet();
    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Update available" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Reload" }));
    });
    expect(activate).toHaveBeenCalledTimes(1);
  });
});
