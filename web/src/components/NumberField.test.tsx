import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, fireEvent, act } from "@testing-library/react";
import { useState } from "react";
import { NumberField } from "./NumberField";

// Just under the 400ms first-repeat delay.
const FIRST_REPEAT_UNDER = 350;

// A real host holding real state: the repeat's whole correctness question is
// whether each tick builds on the last one, which a vi.fn() spy cannot show.
function Host({ start = 0, disabled = false }: { start?: number; disabled?: boolean } = {}) {
  const [value, setValue] = useState(start);
  return <NumberField id="n" label="total eggs" value={value} onChange={setValue} disabled={disabled} />;
}

const field = () => screen.getByRole("spinbutton");
const plus = () => screen.getByRole("button", { name: "Increase total eggs" });
const minus = () => screen.getByRole("button", { name: "Decrease total eggs" });

// Hold for `ms`, then release.
async function hold(button: HTMLElement, ms: number) {
  await act(async () => { fireEvent.pointerDown(button); });
  await act(async () => { vi.advanceTimersByTime(ms); });
  await act(async () => { fireEvent.pointerUp(button); });
}

afterEach(() => vi.useRealTimers());

describe("NumberField", () => {
  it("labels each button with what it changes, not just its direction", () => {
    render(<Host />);
    // "Increase" alone is useless read aloud on a form with six of these.
    expect(plus()).toBeInTheDocument();
    expect(minus()).toBeInTheDocument();
  });

  it("steps by one on a tap and never below zero", async () => {
    vi.useFakeTimers();
    render(<Host start={1} />);

    await hold(plus(), 0);
    expect(field()).toHaveValue(2);

    await hold(minus(), 0);
    await hold(minus(), 0);
    expect(field()).toHaveValue(0);
    // The decrement disables itself at the floor rather than silently no-opping.
    expect(minus()).toBeDisabled();
  });

  it("accelerates while held, each tick building on the last", async () => {
    vi.useFakeTimers();
    render(<Host />);

    // Repeats land at 400 + 60k. Held to 1300ms inclusive, that is k = 0..15,
    // so sixteen of them — the tick at exactly 1300 counts.
    await hold(plus(), 1300);

    // press 1 + ticks 1-10 at +1 (10) + ticks 11-16 at +5 (30) = 41.
    // A hold reaches a real day's count in about a second; +1 alone would need
    // four hundred of them.
    expect(field()).toHaveValue(41);
  });

  it("stays a tap for a short press — no repeat before the delay", async () => {
    vi.useFakeTimers();
    render(<Host />);

    await hold(plus(), FIRST_REPEAT_UNDER);
    expect(field()).toHaveValue(1);
  });

  it("stops on release, and on a pointer that leaves or is cancelled", async () => {
    vi.useFakeTimers();
    render(<Host />);

    // released
    await hold(plus(), 600);
    const afterRelease = (field() as HTMLInputElement).value;
    await act(async () => { vi.advanceTimersByTime(1000); });
    expect(field()).toHaveValue(Number(afterRelease));

    // dragged off the button
    await act(async () => { fireEvent.pointerDown(plus()); });
    await act(async () => { vi.advanceTimersByTime(600); });
    await act(async () => { fireEvent.pointerLeave(plus()); });
    const afterLeave = (field() as HTMLInputElement).value;
    await act(async () => { vi.advanceTimersByTime(1000); });
    expect(field()).toHaveValue(Number(afterLeave));

    // browser took the touch over for a scroll
    await act(async () => { fireEvent.pointerDown(plus()); });
    await act(async () => { vi.advanceTimersByTime(600); });
    await act(async () => { fireEvent.pointerCancel(plus()); });
    const afterCancel = (field() as HTMLInputElement).value;
    await act(async () => { vi.advanceTimersByTime(1000); });
    expect(field()).toHaveValue(Number(afterCancel));
  });

  it("does nothing while disabled", async () => {
    vi.useFakeTimers();
    render(<Host start={5} disabled />);

    await hold(plus(), 1000);
    expect(field()).toHaveValue(5);
    expect(plus()).toBeDisabled();
    expect(field()).toBeDisabled();
  });

  it("still takes a typed value", async () => {
    render(<Host />);
    fireEvent.change(field(), { target: { value: "418" } });
    expect(field()).toHaveValue(418);
  });

  it("leaves no timer running when unmounted mid-hold", async () => {
    vi.useFakeTimers();
    const { unmount } = render(<Host />);

    await act(async () => { fireEvent.pointerDown(plus()); });
    await act(async () => { vi.advanceTimersByTime(600); });
    unmount();

    // A surviving timer would call setState on a dead component; advancing past
    // several more ticks must stay silent.
    const warn = vi.spyOn(console, "error").mockImplementation(() => {});
    await act(async () => { vi.advanceTimersByTime(2000); });
    expect(warn).not.toHaveBeenCalled();
    warn.mockRestore();
  });
});
