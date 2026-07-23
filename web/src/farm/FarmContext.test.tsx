import { describe, it, expect, vi, beforeEach } from "vitest";
import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { FarmProvider } from "./FarmContext";
import { useFarm, useFarmToday } from "./useFarm";
import { getAccount } from "../api/cluckwork";
import { account } from "../test/fixtures";

vi.mock("../api/cluckwork", async () => {
  const actual = await vi.importActual<typeof import("../api/cluckwork")>("../api/cluckwork");
  return { ...actual, getAccount: vi.fn() };
});

const mockGetAccount = vi.mocked(getAccount);

// Reads everything a consumer can see, so one probe covers the provider's whole
// contract.
function Probe() {
  const { farm, loadFailed, refresh } = useFarm();
  const today = useFarmToday();
  return (
    <div>
      <p data-testid="name">{farm?.name ?? "no farm"}</p>
      <p data-testid="today">{today}</p>
      <p data-testid="failed">{String(loadFailed)}</p>
      <button onClick={() => void refresh()}>refresh</button>
    </div>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.useRealTimers();
});

describe("FarmProvider", () => {
  it("holds the shell until /account settles, then supplies the farm", async () => {
    let resolve: ((value: ReturnType<typeof account>) => void) | undefined;
    mockGetAccount.mockReturnValue(new Promise((r) => { resolve = r; }));

    render(<FarmProvider><Probe /></FarmProvider>);

    // Nothing rendered yet: a screen that mounted now would seed its date
    // fields with the browser's today and never correct itself.
    expect(screen.queryByTestId("name")).not.toBeInTheDocument();

    resolve!(account({ name: "Hen House" }));
    expect(await screen.findByTestId("name")).toHaveTextContent("Hen House");
  });

  it("renders the children anyway when /account fails, and says the read failed", async () => {
    mockGetAccount.mockRejectedValue(new Error("offline"));

    render(<FarmProvider><Probe /></FarmProvider>);

    // The shell must not be held hostage by a failed read — the screens under
    // it surface their own errors, and the fallbacks apply. But the flag has to
    // be raised: without it the shell degrades to the DEVICE's day in silence,
    // which is the failure this slice exists to remove (codex review of #123).
    expect(await screen.findByTestId("name")).toHaveTextContent("no farm");
    expect(screen.getByTestId("failed")).toHaveTextContent("true");
  });

  it("clears the failure flag once a retry succeeds", async () => {
    mockGetAccount.mockRejectedValueOnce(new Error("offline"));
    render(<FarmProvider><Probe /></FarmProvider>);
    expect(await screen.findByTestId("failed")).toHaveTextContent("true");

    mockGetAccount.mockResolvedValueOnce(account({ name: "Hen House" }));
    await userEvent.click(screen.getByRole("button", { name: "refresh" }));

    expect(await screen.findByTestId("name")).toHaveTextContent("Hen House");
    expect(screen.getByTestId("failed")).toHaveTextContent("false");
  });

  it("keeps the farm it already had when a later refresh fails", async () => {
    mockGetAccount.mockResolvedValueOnce(account({ name: "Hen House" }));
    render(<FarmProvider><Probe /></FarmProvider>);
    expect(await screen.findByTestId("name")).toHaveTextContent("Hen House");

    mockGetAccount.mockRejectedValueOnce(new Error("offline"));
    await userEvent.click(screen.getByRole("button", { name: "refresh" }));

    // Clearing here would silently move every date field back to browser-local
    // — a stale name is the lesser of the two failures by a wide margin.
    await waitFor(() => expect(mockGetAccount).toHaveBeenCalledTimes(2));
    expect(screen.getByTestId("name")).toHaveTextContent("Hen House");
  });

  it("publishes the new farm after a refresh", async () => {
    mockGetAccount.mockResolvedValueOnce(account({ name: "Hen House" }));
    render(<FarmProvider><Probe /></FarmProvider>);
    expect(await screen.findByTestId("name")).toHaveTextContent("Hen House");

    mockGetAccount.mockResolvedValueOnce(account({ name: "Coop Co" }));
    await userEvent.click(screen.getByRole("button", { name: "refresh" }));

    // This is what makes a settings save show in the sidebar without a reload.
    expect(await screen.findByTestId("name")).toHaveTextContent("Coop Co");
  });

  it("reads /account once for the whole shell, however many consumers there are", async () => {
    mockGetAccount.mockResolvedValue(account());
    render(<FarmProvider><Probe /><Probe /><Probe /></FarmProvider>);
    await screen.findAllByTestId("name");
    expect(mockGetAccount).toHaveBeenCalledTimes(1);
  });
});

describe("useFarmToday", () => {
  it("gives the FARM's day, not the runner's", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    // Still the 15th in UTC; already the 16th in Tokyo.
    vi.setSystemTime(new Date("2026-07-15T23:30:00Z"));
    mockGetAccount.mockResolvedValue(account({ timeZoneId: "Asia/Tokyo" }));

    render(<FarmProvider><Probe /></FarmProvider>);

    expect(await screen.findByTestId("today")).toHaveTextContent("2026-07-16");
    vi.useRealTimers();
  });

  it("falls back to browser-local outside a provider", () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(2026, 6, 15, 12, 0, 0)); // local Jul 15, 2026
    // The default context is the app's real answer for "farm unknown", not a
    // test-only stub: the same value applies before /account resolves and after
    // one that failed.
    render(<Probe />);
    expect(screen.getByTestId("today")).toHaveTextContent("2026-07-15");
    expect(screen.getByTestId("name")).toHaveTextContent("no farm");
    vi.useRealTimers();
  });

  it("does not blow up when the default refresh is called", async () => {
    render(<Probe />);
    await userEvent.click(screen.getByRole("button", { name: "refresh" }));
    expect(mockGetAccount).not.toHaveBeenCalled();
  });
});

describe("the farm's day rolling over", () => {
  it("starts offering the new day without a reload", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    // 30 seconds before midnight in Tokyo.
    vi.setSystemTime(new Date("2026-07-15T14:59:30Z"));
    mockGetAccount.mockResolvedValue(account({ timeZoneId: "Asia/Tokyo" }));

    render(<FarmProvider><Probe /></FarmProvider>);
    expect(await screen.findByTestId("today")).toHaveTextContent("2026-07-15");

    // Past Tokyo midnight. Nothing re-renders on the clock alone, so without
    // the provider's tick a tab left open would keep capping date fields at
    // yesterday (codex review of #123).
    await act(async () => {
      vi.setSystemTime(new Date("2026-07-15T15:01:00Z"));
      await vi.advanceTimersByTimeAsync(61_000);
    });

    expect(screen.getByTestId("today")).toHaveTextContent("2026-07-16");
    vi.useRealTimers();
  });

  it("does not re-render the shell on a tick that changes nothing", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date("2026-07-15T12:00:00Z"));
    mockGetAccount.mockResolvedValue(account({ timeZoneId: "UTC" }));

    let renders = 0;
    function Counter() {
      renders += 1;
      return <p data-testid="today">{useFarmToday()}</p>;
    }
    render(<FarmProvider><Counter /></FarmProvider>);
    await screen.findByTestId("today");
    const settled = renders;

    // Five minutes of ticks inside the same farm day. React bails out when the
    // string is unchanged, so the poll costs a comparison and nothing else.
    await act(async () => { await vi.advanceTimersByTimeAsync(5 * 60_000); });

    expect(renders).toBe(settled);
    vi.useRealTimers();
  });
});
