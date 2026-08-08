import { useCallback, useEffect, useRef } from "react";
import type { Dispatch, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { Minus, Plus } from "lucide-react";

// Hold-to-repeat: one press, then a pause so a tap stays a tap, then ticks.
const FIRST_REPEAT_MS = 400;
const REPEAT_EVERY_MS = 60;

// The stride grows relative to the BASE step (#444's `step` prop — 1 egg by
// default, or a farm/user's chosen pack unit, e.g. 30 for Tray) rather than a
// fixed count. A flock's day runs to several hundred eggs, and machine-gunning
// +1 either takes for ever or overshoots wildly; widening the stride gets
// there in a couple of seconds and still lands on a round multiple of the
// base unit the counter can correct by tapping once.
const strideFor = (tick: number) => (tick > 30 ? 10 : tick > 10 ? 5 : 1);

interface NumberFieldProps {
  /** Ties the input to its own <label htmlFor>. */
  id: string;
  /** Names the two buttons — "Increase total eggs", not "Increase". */
  label: string;
  value: number;
  /** Takes React's setState directly: the repeat MUST use the updater form. */
  onChange: Dispatch<SetStateAction<number>>;
  /**
   * #444 — the amount +/− (and the first tick of a hold) move by; the
   * hold-repeat's growing stride (see strideFor) then multiplies THIS, not a
   * hardcoded 1. Defaults to 1 (every caller but Daily Entry's grade/loss
   * steppers). Typing stays a plain number regardless — only the guided
   * control counts by units.
   */
  step?: number;
  /**
   * Ceiling for the + button and its repeat. Typing is deliberately NOT capped:
   * a draft is allowed to be over-graded while the counter rearranges it, and
   * only the guided control refuses to create that state in the first place.
   */
  max?: number;
  /**
   * Floor for the − button, its repeat, AND typing (#250). Unlike the ceiling,
   * typing IS clamped: there is no rearranging-a-draft case below the floor —
   * a sale line of zero is not a lesser value being reshuffled, it is a
   * meaningless row. Defaults to 0, the counts' natural floor.
   */
  min?: number;
  disabled?: boolean;
}

// F134: number entry for the barn. The native spinner is a 10px hit target and
// vanishes entirely on touch, so counts get typed on a phone keypad one digit
// at a time. These are thumb-sized, and holding one accelerates.
export function NumberField({
  id, label, value, onChange, step = 1, max = Number.POSITIVE_INFINITY, min = 0, disabled = false,
}: NumberFieldProps) {
  const { t } = useTranslation("numberField");
  const timer = useRef<number | null>(null);
  const tick = useRef(0);
  // A press fires once but its repeat outlives the render that started it. Both
  // of these can move mid-hold — a prefill landing changes `sellable`, and it
  // can also discover a submitted entry and lock the form — so the repeat reads
  // them live instead of from the closure it was created in. An earlier version
  // captured them and argued `max` was invariant during a hold; it is not.
  const live = useRef({ value, step, max, min, disabled });
  live.current = { value, step, max, min, disabled };

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
    // `stride` multiplies the CURRENT `step` (live, like max/min below) rather
    // than a bare count — #444 needs a hold that started at step=1 to keep
    // accelerating correctly if the farm's stepper unit changes mid-render.
    const bump = (stride: number) =>
      onChange((prev) => Math.min(
        live.current.max, Math.max(live.current.min, prev + direction * stride * live.current.step)));

    bump(1); // the press itself lands immediately
    const repeat = () => {
      // Read live, and BEFORE bumping: a repeat wedged against the floor or the
      // ceiling should terminate rather than reschedule for ever. The updater
      // cannot report this — React defers it, so anything it sets is still
      // unset when the call returns.
      const { value: now, max: ceiling, min: floor, disabled: locked } = live.current;
      if (locked || (direction > 0 ? now >= ceiling : now <= floor)) {
        stop();
        return;
      }
      tick.current += 1;
      bump(strideFor(tick.current));
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
      onChange((prev) => Math.min(
        live.current.max, Math.max(live.current.min, prev + direction * live.current.step)));
    },
  });

  // #444 — a bare −/+ is a mystery button once a tap moves 30 eggs, so a
  // non-1 step is spelled out ON the control ("−30"/"+30"), at the point of
  // touch, and the accessible names carry the amount too. The wider button is
  // a better glove target, not a cost. Step 1 keeps the plain icons — "+1"
  // everywhere would be noise restating the default.
  const unitStep = step > 1;
  return (
    <span className="numfield">
      <button type="button"
        className={`numfield-step${unitStep ? " numfield-step-unit" : ""}`}
        disabled={disabled || value <= min}
        aria-label={unitStep ? t("decreaseByLabel", { label, step }) : t("decreaseLabel", { label })}
        {...held(-1)}>
        {unitStep ? <span aria-hidden>−{step}</span> : <Minus size={16} aria-hidden />}
      </button>
      <input
        id={id}
        type="number"
        // -Infinity means "no floor" (signed adjustments); min="-Infinity" is
        // not a valid HTML constraint, so the attribute is omitted entirely.
        min={Number.isFinite(min) ? min : undefined}
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(Math.max(min, e.target.valueAsNumber || 0))}
      />
      <button type="button"
        className={`numfield-step${unitStep ? " numfield-step-unit" : ""}`}
        aria-label={unitStep ? t("increaseByLabel", { label, step }) : t("increaseLabel", { label })}
        disabled={disabled || value >= max} {...held(1)}>
        {unitStep ? <span aria-hidden>+{step}</span> : <Plus size={16} aria-hidden />}
      </button>
    </span>
  );
}
