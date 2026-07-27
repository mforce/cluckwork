import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, fireEvent, act } from "@testing-library/react";
import { useState } from "react";
import { NumberField } from "./NumberField";
import i18n from "../i18n";

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

// F134: the + is capped where adding more would make the day invalid — the
// counter should not be able to build an over-graded entry with the guided
// control. Typing stays free: a draft may be over-graded while it is rearranged.
describe("NumberField ceiling", () => {
  function Capped({ start = 0, max }: { start?: number; max: number }) {
    const [value, setValue] = useState(start);
    // Same label as Host, so the shared plus()/field() queries apply.
    return <NumberField id="n" label="total eggs" value={value} onChange={setValue} max={max} />;
  }

  it("disables + on arrival at the ceiling", async () => {
    vi.useFakeTimers();
    render(<Capped start={9} max={10} />);

    await hold(plus(), 0);
    expect(field()).toHaveValue(10);
    expect(plus()).toBeDisabled();
  });

  it("stops a hold dead at the ceiling instead of running past it", async () => {
    vi.useFakeTimers();
    render(<Capped start={0} max={12} />);

    // Long enough to add ~40 unclamped.
    await hold(plus(), 1300);
    expect(field()).toHaveValue(12);
  });

  it("still lets the value be typed past the ceiling", async () => {
    render(<Capped start={0} max={10} />);
    fireEvent.change(field(), { target: { value: "25" } });
    expect(field()).toHaveValue(25);
    // and the + refuses to make it worse
    expect(plus()).toBeDisabled();
  });
});

// Two fingers on the same field — one on −, one on + — fire two pointerDowns
// with no stop between them. This is a phone-first screen, so it is reachable.
describe("NumberField overlapping presses", () => {
  it("leaves no orphaned timer when a second press starts before the first stops", async () => {
    vi.useFakeTimers();
    render(<Host start={100} />);

    await act(async () => { fireEvent.pointerDown(plus()); });
    await act(async () => { vi.advanceTimersByTime(600); });
    // second finger lands without the first having lifted
    await act(async () => { fireEvent.pointerDown(minus()); });
    await act(async () => { vi.advanceTimersByTime(600); });
    // both lifted
    await act(async () => { fireEvent.pointerUp(minus()); });
    await act(async () => { fireEvent.pointerUp(plus()); });

    const settled = (field() as HTMLInputElement).value;
    // Nothing may still be counting: an orphaned repeat would keep running
    // until unmount, silently rewriting the count in the barn.
    await act(async () => { vi.advanceTimersByTime(3000); });
    expect(field()).toHaveValue(Number(settled));
  });
});

// Enter and Space on a focused button dispatch `click`, never `pointerdown`.
// Pointer-only handlers therefore leave these buttons entirely dead to the
// keyboard and to assistive technology (codex review of PR #137).
describe("NumberField keyboard", () => {
  it("steps on keyboard activation, which arrives as a click", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    render(<Host start={4} />);

    plus().focus();
    await user.keyboard("{Enter}");
    expect(field()).toHaveValue(5);

    await user.keyboard(" ");
    expect(field()).toHaveValue(6);

    minus().focus();
    await user.keyboard("{Enter}");
    expect(field()).toHaveValue(5);
  });

  it("does not double-count a pointer press, which also emits a click", async () => {
    vi.useFakeTimers();
    render(<Host start={0} />);

    await act(async () => { fireEvent.pointerDown(plus()); });
    await act(async () => { fireEvent.pointerUp(plus()); });
    await act(async () => { fireEvent.click(plus()); }); // the browser's own follow-up

    expect(field()).toHaveValue(1);
  });
});

describe("NumberField live limits", () => {
  // The repeat outlives the render that started it, so it must not hold on to
  // the ceiling or the disabled flag it was created with.
  function Shrinking() {
    const [value, setValue] = useState(0);
    const [max, setMax] = useState(1000);
    return (
      <>
        <NumberField id="n" label="total eggs" value={value} onChange={setValue} max={max} />
        <button onClick={() => setMax(5)}>shrink</button>
      </>
    );
  }

  it("obeys a ceiling that drops mid-hold", async () => {
    vi.useFakeTimers();
    render(<Shrinking />);

    await act(async () => { fireEvent.pointerDown(plus()); });
    await act(async () => { vi.advanceTimersByTime(500); });
    // e.g. a prefill lands and lowers the sellable count under the hold
    await act(async () => { fireEvent.click(screen.getByText("shrink")); });
    await act(async () => { vi.advanceTimersByTime(2000); });
    await act(async () => { fireEvent.pointerUp(plus()); });

    expect(Number((field() as HTMLInputElement).value)).toBeLessThanOrEqual(5);
  });

  it("stops doing work once it is wedged against a limit", async () => {
    vi.useFakeTimers();
    // Counting updates rather than timers: React keeps scheduler timeouts of
    // its own, so a raw timer count asserts an implementation detail we do not
    // own. What matters is that a wedged repeat stops calling back.
    const onChange = vi.fn();
    function Counting() {
      const [value, setValue] = useState(2);
      return (
        <NumberField id="n" label="total eggs" value={value}
          onChange={(next) => { onChange(); setValue(next); }} />
      );
    }
    render(<Counting />);

    await act(async () => { fireEvent.pointerDown(minus()); });
    await act(async () => { vi.advanceTimersByTime(2000); });
    expect(field()).toHaveValue(0);

    const settled = onChange.mock.calls.length;
    await act(async () => { vi.advanceTimersByTime(10_000); });
    expect(onChange).toHaveBeenCalledTimes(settled);

    await act(async () => { fireEvent.pointerUp(minus()); });
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 8, batch B1)
// ---------------------------------------------------------------------------

// `numberField` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language the rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render. Asserting that text — even under a non-English locale — would prove
// nothing (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at
// runtime instead — the same i18n.addResource technique the nav wiring tests
// use (AppLayout.test.tsx, Task 7) — so the marker only renders if NumberField
// actually reads the catalog.
describe("NumberField i18n wiring (#182, Task 8)", () => {
  function withOverride(key: string, value: string, run: () => void) {
    const original = i18n.getResource("en", "numberField", key) as string;
    i18n.addResource("en", "numberField", key, value);
    try {
      run();
    } finally {
      i18n.addResource("en", "numberField", key, original);
    }
  }

  it("reads the increase/decrease aria-labels from the catalog, not hardcoded literals", () => {
    withOverride("increaseLabel", "INCREASE-MARKER {{label}}", () => {
      withOverride("decreaseLabel", "DECREASE-MARKER {{label}}", () => {
        render(<Host />);
        expect(screen.getByRole("button", { name: "INCREASE-MARKER total eggs" })).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "DECREASE-MARKER total eggs" })).toBeInTheDocument();
        expect(screen.queryByRole("button", { name: "Increase total eggs" })).not.toBeInTheDocument();
        expect(screen.queryByRole("button", { name: "Decrease total eggs" })).not.toBeInTheDocument();
      });
    });
  });
});
