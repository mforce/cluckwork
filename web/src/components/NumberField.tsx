import { useCallback, useEffect, useRef } from "react";
import type { Dispatch, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { Minus, Plus } from "lucide-react";

// Hold-to-repeat: one press, then a pause so a tap stays a tap, then ticks.
const FIRST_REPEAT_MS = 400;
const REPEAT_EVERY_MS = 60;

// The step grows rather than the rate. A flock's day runs to several hundred
// eggs, and machine-gunning +1 either takes for ever or overshoots wildly;
// widening the stride gets there in a couple of seconds and still lands on a
// round number the counter can correct by tapping once.
const stepFor = (tick: number) => (tick > 30 ? 10 : tick > 10 ? 5 : 1);

interface NumberFieldProps {
  /** Ties the input to its own <label htmlFor>. */
  id: string;
  /** Names the two buttons — "Increase total eggs", not "Increase". */
  label: string;
  value: number;
  /** Takes React's setState directly: the repeat MUST use the updater form. */
  onChange: Dispatch<SetStateAction<number>>;
  /**
   * Ceiling for the + button and its repeat. Typing is deliberately NOT capped:
   * a draft is allowed to be over-graded while the counter rearranges it, and
   * only the guided control refuses to create that state in the first place.
   */
  max?: number;
  disabled?: boolean;
}

// F134: number entry for the barn. The native spinner is a 10px hit target and
// vanishes entirely on touch, so counts get typed on a phone keypad one digit
// at a time. These are thumb-sized, and holding one accelerates.
export function NumberField({
  id, label, value, onChange, max = Number.POSITIVE_INFINITY, disabled = false,
}: NumberFieldProps) {
  const { t } = useTranslation("numberField");
  const timer = useRef<number | null>(null);
  const tick = useRef(0);
  // A press fires once but its repeat outlives the render that started it. Both
  // of these can move mid-hold — a prefill landing changes `sellable`, and it
  // can also discover a submitted entry and lock the form — so the repeat reads
  // them live instead of from the closure it was created in. An earlier version
  // captured them and argued `max` was invariant during a hold; it is not.
  const live = useRef({ value, max, disabled });
  live.current = { value, max, disabled };

  // Enter and Space on a focused button dispatch `click`, never `pointerdown`,
  // so pointer-only handlers leave these buttons completely dead to the
  // keyboard. A pointer press also fires click after pointerup, hence the flag.
  const pointerDrove = useRef(false);

  const stop = useCallback(() => {
    if (timer.current !== null) window.clearTimeout(timer.current);
    timer.current = null;
    tick.current = 0;
  }, []);

  // Unmounting mid-hold (a route change, a save that swaps the form) would
  // otherwise leave a timer ticking against a dead component.
  useEffect(() => stop, [stop]);

  const start = useCallback((direction: 1 | -1) => {
    if (disabled) return;
    // Two fingers on the same field — one on −, one on + — fire two pointerDowns
    // with no stop between them, and this is a phone-first screen. Without this
    // the first timer is orphaned: stop() only ever clears the CURRENT handle,
    // so the older repeat would keep counting until the component unmounted.
    stop();
    // The updater form is not optional: every repeat reads the value the last
    // one produced, and a captured `value` would make each tick recompute from
    // the number that was on screen when the press began.
    // Clamped inside the updater so a hold stops dead at the ceiling instead of
    // running past it. `max` is captured, which is correct: for a grade it is
    // (this field + what is unallocated), and that sum does not move while this
    // field is the one being incremented.
    const bump = (by: number) =>
      onChange((prev) => Math.min(live.current.max, Math.max(0, prev + direction * by)));

    bump(1); // the press itself lands immediately
    const repeat = () => {
      // Read live, and BEFORE bumping: a repeat wedged against the floor or the
      // ceiling should terminate rather than reschedule for ever. The updater
      // cannot report this — React defers it, so anything it sets is still
      // unset when the call returns.
      const { value: now, max: ceiling, disabled: locked } = live.current;
      if (locked || (direction > 0 ? now >= ceiling : now <= 0)) {
        stop();
        return;
      }
      tick.current += 1;
      bump(stepFor(tick.current));
      timer.current = window.setTimeout(repeat, REPEAT_EVERY_MS);
    };
    timer.current = window.setTimeout(repeat, FIRST_REPEAT_MS);
  }, [disabled, onChange, stop]);

  // pointer* rather than mouse*, so one set of handlers covers touch and pen.
  // Cancel and leave both stop it: dragging off a held button must not leave it
  // counting, and neither must a touch the browser takes over for a scroll.
  const held = (direction: 1 | -1) => ({
    onPointerDown: () => { pointerDrove.current = true; start(direction); },
    onPointerUp: stop,
    onPointerLeave: stop,
    onPointerCancel: stop,
    // Keyboard and assistive-technology activation only ever reach us here.
    onClick: () => {
      if (pointerDrove.current) { pointerDrove.current = false; return; }
      if (disabled) return;
      onChange((prev) => Math.min(live.current.max, Math.max(0, prev + direction)));
    },
  });

  return (
    <span className="numfield">
      <button type="button" className="numfield-step" disabled={disabled || value <= 0}
        aria-label={t("decreaseLabel", { label })} {...held(-1)}>
        <Minus size={16} aria-hidden />
      </button>
      <input
        id={id}
        type="number"
        min={0}
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(Math.max(0, e.target.valueAsNumber || 0))}
      />
      <button type="button" className="numfield-step"
        aria-label={t("increaseLabel", { label })} disabled={disabled || value >= max} {...held(1)}>
        <Plus size={16} aria-hidden />
      </button>
    </span>
  );
}
