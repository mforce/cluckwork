import { useCallback, useEffect, useRef } from "react";
import type { Dispatch, SetStateAction } from "react";
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
  disabled?: boolean;
}

// F134: number entry for the barn. The native spinner is a 10px hit target and
// vanishes entirely on touch, so counts get typed on a phone keypad one digit
// at a time. These are thumb-sized, and holding one accelerates.
export function NumberField({ id, label, value, onChange, disabled = false }: NumberFieldProps) {
  const timer = useRef<number | null>(null);
  const tick = useRef(0);

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
    // The updater form is not optional: every repeat reads the value the last
    // one produced, and a captured `value` would make each tick recompute from
    // the number that was on screen when the press began.
    const bump = (by: number) => onChange((prev) => Math.max(0, prev + direction * by));

    bump(1); // the press itself lands immediately
    const repeat = () => {
      tick.current += 1;
      bump(stepFor(tick.current));
      timer.current = window.setTimeout(repeat, REPEAT_EVERY_MS);
    };
    timer.current = window.setTimeout(repeat, FIRST_REPEAT_MS);
  }, [disabled, onChange]);

  // pointer* rather than mouse*, so one set of handlers covers touch and pen.
  // Cancel and leave both stop it: dragging off a held button must not leave it
  // counting, and neither must a touch the browser takes over for a scroll.
  const held = (direction: 1 | -1) => ({
    onPointerDown: () => start(direction),
    onPointerUp: stop,
    onPointerLeave: stop,
    onPointerCancel: stop,
  });

  return (
    <span className="numfield">
      <button type="button" className="numfield-step" disabled={disabled || value <= 0}
        aria-label={`Decrease ${label}`} {...held(-1)}>
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
        aria-label={`Increase ${label}`} disabled={disabled} {...held(1)}>
        <Plus size={16} aria-hidden />
      </button>
    </span>
  );
}
