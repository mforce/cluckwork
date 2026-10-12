import { useEffect, useRef, useState } from "react";
import type { Dispatch, FormEvent, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { createFlock, listFlocks } from "../../api/cluckwork";
import type { Flock } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { Dialog } from "../../components/Dialog";
import { DialogError } from "../../components/DialogError";
import { NumberField } from "../../components/NumberField";
import type { DialogErrors } from "../../components/useDialogErrors";
import { useAuth } from "../../auth/useAuth";
import { useFarmToday } from "../../farm/useFarm";
import { newId } from "../../lib/ids";
import { capturable } from "./helpers";

interface NewFlockDeps {
  run: <T>(scope: string, action: (current: () => boolean) => Promise<T>) => Promise<T | undefined>;
  busy: boolean;
  isPending: (scope: string) => boolean;
  errors: DialogErrors;
  dismissDialog: (scope: "new-flock") => void;
  idFor: (name: string) => string;
  retarget: (apply: () => void) => void;
  setFlockId: (id: string) => void;
  setPickerFlock: Dispatch<SetStateAction<Flock | null>>;
  setPickerFlockGen: Dispatch<SetStateAction<number>>;
  setFlocks: Dispatch<SetStateAction<Flock[]>>;
}

// The new-flock dialog of the daily-entry screen and its create write. All
// flight discipline and the dialog session stay the page's one useDialogAction
// (`action`), and every retarget goes through the page's `retarget` so the
// disarm discipline of #403 round 4 is unchanged. `idFor` is the page's useId
// namespace so the rendered ids are identical to the pre-split DOM.
export function useNewFlock({
  run, busy, isPending, errors, dismissDialog,
  idFor, retarget, setFlockId, setPickerFlock, setPickerFlockGen, setFlocks,
}: NewFlockDeps) {
  const { t } = useTranslation(["dailyEntry", "dashboard"]);
  const { t: tc } = useTranslation("common");
  const { isAdmin } = useAuth();
  const today = useFarmToday();

  // inline flock creation
  const [showNewFlock, setShowNewFlock] = useState(false);
  const [newFlockName, setNewFlockName] = useState("");
  const [newFlockBreed, setNewFlockBreed] = useState("");
  const [newFlockPlaced, setNewFlockPlaced] = useState(today);
  const [newFlockCount, setNewFlockCount] = useState(100);

  // Stable idempotency key for the create — the page's keys follow the same
  // rule: regenerated only after a definitive success, so a retry after an
  // ambiguous network failure dedupes server-side instead of repeating the
  // write.
  const flockKey = useRef<string>(newId());

  // Dismissal empties the dialog's slot and mutes the attempt still out, so a
  // late failure is not reported against a session the user reopened.
  const closeNewFlock = () => { setShowNewFlock(false); dismissDialog("new-flock"); };

  // #703 review r2 — the dialog is admin-gated (`open={showNewFlock && isAdmin}`),
  // so a role change HIDES it without firing onClose: an in-flight create would
  // stay `current()` and could reset/close/retarget the dialog a re-promotion
  // restores. End the session on the isAdmin edge (dismiss mutes + advances the
  // generation; setShowNewFlock hides it), so a re-promotion reopens a fresh one.
  useEffect(() => {
    if (!isAdmin) { setShowNewFlock(false); dismissDialog("new-flock"); }
  }, [isAdmin, dismissDialog]);

  async function onCreateFlock(e: FormEvent) {
    e.preventDefault();
    // #236: this form shipped with NO in-flight guard at all — a double submit
    // reached the API twice. The hook's ref is the guard now.
    await run("new-flock", async (current) => {
      const created = await createFlock({
        name: newFlockName,
        breed: newFlockBreed,
        placementDate: newFlockPlaced,
        initialCount: newFlockCount,
      }, flockKey.current);
      flockKey.current = newId();
      // Best-effort refresh of the picker's eligible-list rows (RUN — the flock
      // exists whether or not anyone is watching). FIRE-AND-FORGET (#703 review
      // r2): a slow or hung list read must never block the retarget/exact-GET
      // hydration below — the base hydrated first, and awaiting here gated flock
      // selection on the refresh (client.ts has no timeout). Its rejection lands
      // nowhere; the requestedId-driven hydration below is independent of it.
      void listFlocks().then((f) => setFlocks(capturable(f))).catch(() => {});
      // Superseded (#703 INV-8, owner GATE 2026-09-06): the flock exists and
      // the list shows it, but switching the page's captured flock and
      // re-hydrating the picker belong to whatever session is on screen now —
      // the replacement's user did not ask to create this flock.
      if (!current()) return;
      // Through `retarget` like the pickers: creating a flock switches the
      // captured day too, and nothing stops the dialog being opened while the
      // remainder gesture is armed (#403 round 4).
      retarget(() => setFlockId(created.id));
      // #512 (T036) — createFlock's response is `Created` ({ id }) only, not a
      // full Flock. `pickerFlock` goes to null so `requestedId` becomes
      // `created.id`, which drives FlockPicker's real `getFlock` exact-GET read;
      // `handleFlockSnapshot` mirrors that resolved entity onto `pickerFlock`
      // once it lands.
      setPickerFlock(null);
      setPickerFlockGen((g) => g + 1);
      setShowNewFlock(false);
      setNewFlockName("");
      setNewFlockBreed("");
      setNewFlockPlaced(today);
      setNewFlockCount(100);
    });
  }

// F131: creating a flock is catalog work, not capture — it belongs in a
// dialog like every other create, instead of shoving the entry grid
// down the page the moment the picker has nothing to offer yet.
// Admin-gated (#388): a scoped Worker cannot assign the flock it just
// created, so a role change mid-dialog closes it too.
  const dialog = (
    <Dialog open={showNewFlock && isAdmin} title={t("newFlockDialogTitle")} onClose={closeNewFlock}>
      <form className="inline-form" onSubmit={onCreateFlock}>
        <label>{t("nameLabel")}
          <input value={newFlockName} required
            onChange={(e) => setNewFlockName(e.target.value)} />
        </label>
        <label>{t("breedLabel")}
          <input value={newFlockBreed} required
            onChange={(e) => setNewFlockBreed(e.target.value)} />
        </label>
        <label>{t("placedLabel")}
          <input type="date" value={newFlockPlaced} max={today} required
            onChange={(e) => setNewFlockPlaced(e.target.value)} />
        </label>
        {/* #250: the one count on this page that was still a raw input.
            Sibling label, not wrapping — a <label> may not contain
            interactive content other than its own control, and the stepper
            carries two buttons. */}
        <div className="numfield-field">
          <label htmlFor={idFor("new-flock-birds")}>{t("birdsLabel")}</label>
          <NumberField id={idFor("new-flock-birds")} label={t("birdsLabel").toLowerCase()}
            value={newFlockCount} onChange={setNewFlockCount} min={1} />
        </div>
        {/* #479 — this form's own failures live in the "new-flock" dialog
            slot; DialogError renders them only while this dialog is open.
            (Used to read `!showNewFlock` on a shared slot instead, inside a
            dialog that only exists WHEN showNewFlock — so a failed create
            rendered no error anywhere and the button just appeared to do
            nothing; F134 review of #131.) */}
        <DialogError errors={errors} scope="new-flock" />
        <div className="dialog-foot">
          <button type="button" className="link" onClick={closeNewFlock}>{tc("cancel")}</button>
          <BusyButton variant="contained" type="submit" busy={isPending("new-flock")} disabled={busy}>
            {t("createFlockButton")}
          </BusyButton>
        </div>
      </form>
    </Dialog>
  );

  return { setShowNewFlock, dialog };
}
