import { useId } from "react";
import type { Dispatch, FormEvent, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { DialogActions, Stack, TextField } from "@mui/material";
import { createFlock, recordBirdMovement, updateFlock } from "../api/cluckwork";
import type { BirdMovement } from "../api/cluckwork";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { DialogError } from "../components/DialogError";
import { NumberField } from "../components/NumberField";
import type { DialogErrors } from "../components/useDialogErrors";
import { usePagedList } from "../components/usePagedList";
import { flockMovementLabel } from "../i18n/enums";

// The three write dialogs of the flocks screen (F131/#703) and their submit
// handlers. All dialog state, the flight guard and the write discipline stay
// on FlocksPage — this renders from it, so the dialogs share the page's one
// useDialogAction session and message slots (#703) and each renders its own
// failure through its own slot (#479).
export function FlockDialogs({
  creating, setCreating,
  editingId, setEditingId,
  isAdmin,
  recording, setRecording,
  busy,
  isPending,
  errors,
  run,
  commit,
  ledger,
  ledgerFlockId,
  today,
  name, setName,
  breed, setBreed,
  placed, setPlaced,
  count, setCount,
  editName, setEditName,
  editBreed, setEditBreed,
  editPlaced, setEditPlaced,
  editCount, setEditCount,
  mvDate, setMvDate,
  mvType, setMvType,
  mvQty, setMvQty,
  mvNote, setMvNote,
  closeCreate,
  closeEdit,
  closeRecordMovement,
}: {
  creating: boolean; setCreating: Dispatch<SetStateAction<boolean>>;
  editingId: string | null; setEditingId: Dispatch<SetStateAction<string | null>>;
  isAdmin: boolean;
  recording: boolean; setRecording: Dispatch<SetStateAction<boolean>>;
  busy: boolean;
  isPending: (scope: string) => boolean;
  errors: DialogErrors;
  run: <T>(scope: string, action: (current: () => boolean) => Promise<T>) => Promise<T | undefined>;
  commit: (keyScope: string, action: (key: string) => Promise<unknown>) => Promise<void>;
  ledger: ReturnType<typeof usePagedList<BirdMovement>>;
  ledgerFlockId: string | null;
  today: string;
  name: string; setName: Dispatch<SetStateAction<string>>;
  breed: string; setBreed: Dispatch<SetStateAction<string>>;
  placed: string; setPlaced: Dispatch<SetStateAction<string>>;
  count: number; setCount: Dispatch<SetStateAction<number>>;
  editName: string; setEditName: Dispatch<SetStateAction<string>>;
  editBreed: string; setEditBreed: Dispatch<SetStateAction<string>>;
  editPlaced: string; setEditPlaced: Dispatch<SetStateAction<string>>;
  editCount: number; setEditCount: Dispatch<SetStateAction<number>>;
  mvDate: string; setMvDate: Dispatch<SetStateAction<string>>;
  mvType: string; setMvType: Dispatch<SetStateAction<string>>;
  mvQty: number; setMvQty: Dispatch<SetStateAction<number>>;
  mvNote: string; setMvNote: Dispatch<SetStateAction<string>>;
  closeCreate: () => void;
  closeEdit: () => void;
  closeRecordMovement: () => void;
}) {
  const { t } = useTranslation("flocks");
  const { t: tc } = useTranslation("common");
  // NumberField owns its own input, so labels point at it by id (#250).
  const fieldId = useId();
  const idFor = (name: string) => `${fieldId}-${name}`;

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    await run("create", async (current) => {
      await commit("create-flock", (key) =>
        createFlock({ name, breed, placementDate: placed, initialCount: count }, key));
      // Superseded: the flock exists and the list shows it, but the form and
      // its dialog belong to whatever session is on screen now (#703).
      if (!current()) return;
      setName("");
      setBreed("");
      setPlaced(today);
      setCount(100);
      setCreating(false);
    });
  }

  async function onSaveEdit(e: FormEvent) {
    e.preventDefault();
    const id = editingId;
    if (id === null) return;
    // The run scope is the dialog's; the idempotency key stays per flock.
    await run("edit", async (current) => {
      await commit(`update:${id}`, (key) =>
        updateFlock(id, {
          name: editName, breed: editBreed,
          placementDate: editPlaced, initialCount: editCount,
        }, key));
      if (!current()) return;
      setEditingId(null);
    });
  }

  async function onRecordMovement(e: FormEvent) {
    e.preventDefault();
    if (!ledgerFlockId) return;
    const id = ledgerFlockId;
    await run("record-movement", async (current) => {
      await commit(`movement:${id}`, async (key) => {
        await ledger.runWrite(async () => {
          await recordBirdMovement(id, {
            date: mvDate, type: mvType, quantity: mvQty,
            note: mvNote || undefined,
          }, key);
        });
      });
      if (!current()) return;
      setMvQty(1);
      setMvNote("");
      setRecording(false);
    });
  }

  return (
    <>
      <Dialog open={creating && isAdmin} title={t("newFlockDialogTitle")} onClose={closeCreate}
        actions={(
          <DialogActions>
            <button type="button" className="link" onClick={closeCreate}>{tc("cancel")}</button>
            <BusyButton variant="contained" type="submit" busy={isPending("create")} disabled={busy}>{t("addFlockButton")}</BusyButton>
          </DialogActions>
        )}
        formProps={{ onSubmit: onCreate }}
      >
        <Stack spacing={2}>
          <TextField
            label={t("nameLabel")}
            value={name}
            slotProps={{ htmlInput: { maxLength: 100, required: true } }}
            onChange={(e) => setName(e.target.value)}
          />
          <TextField
            label={t("breedLabel")}
            value={breed}
            slotProps={{ htmlInput: { maxLength: 100, required: true } }}
            onChange={(e) => setBreed(e.target.value)}
          />
          <TextField
            label={t("placedLabel")}
            type="date"
            value={placed}
            slotProps={{ htmlInput: { max: today, required: true }, inputLabel: { shrink: true } }}
            onChange={(e) => setPlaced(e.target.value)}
          />
          {/* #250: sibling label, not wrapping — a <label> may not contain
              interactive content other than its own control, and the stepper
              carries two buttons. NumberField itself is out of this slice's
              scope (pair 3, #828), so it and its wrapper keep their existing
              markup unconverted. */}
          <div className="numfield-field">
            <label htmlFor={idFor("birds")}>{t("birdsLabel")}</label>
            <NumberField id={idFor("birds")} label={t("birdsLabel").toLowerCase()}
              value={count} onChange={setCount} min={1} />
          </div>
          <DialogError errors={errors} scope="create" />
        </Stack>
      </Dialog>

      {/* Editing is admin-only, so a role change mid-edit closes it. */}
      <Dialog open={editingId !== null && isAdmin} title={t("editFlockDialogTitle")} onClose={closeEdit}
        actions={(
          <DialogActions>
            <button type="button" className="link" onClick={closeEdit}>{tc("cancel")}</button>
            <BusyButton variant="contained" type="submit" busy={isPending("edit")} disabled={busy}>
              {tc("save")}
            </BusyButton>
          </DialogActions>
        )}
        formProps={{ onSubmit: onSaveEdit, noValidate: true }}
      >
        {/* noValidate: the row's save used to be a plain button — native
            constraint validation never ran on these fields. */}
        <Stack spacing={2}>
          <TextField
            label={t("editNameLabel")}
            value={editName}
            slotProps={{ htmlInput: { maxLength: 100 } }}
            onChange={(e) => setEditName(e.target.value)}
          />
          <TextField
            label={t("editBreedLabel")}
            value={editBreed}
            slotProps={{ htmlInput: { maxLength: 100 } }}
            onChange={(e) => setEditBreed(e.target.value)}
          />
          <TextField
            label={t("editPlacedLabel")}
            type="date"
            value={editPlaced}
            slotProps={{ htmlInput: { max: today }, inputLabel: { shrink: true } }}
            onChange={(e) => setEditPlaced(e.target.value)}
          />
          <div className="numfield-field">
            <label htmlFor={idFor("edit-count")}>{t("editCountLabel")}</label>
            <NumberField id={idFor("edit-count")} label={t("editCountLabel").toLowerCase()}
              value={editCount} onChange={setEditCount} min={1} />
          </div>
          <DialogError errors={errors} scope="edit" />
        </Stack>
      </Dialog>

      {ledgerFlockId && (
            <Dialog open={recording && isAdmin} title={t("recordMovementDialogTitle")} onClose={closeRecordMovement}
              actions={(
                <DialogActions>
                  <button type="button" className="link" onClick={closeRecordMovement}>{tc("cancel")}</button>
                  <BusyButton variant="contained" type="submit"
                    busy={isPending("record-movement")}
                    disabled={busy || mvQty === 0}>
                    {t("recordButton")}
                  </BusyButton>
                </DialogActions>
              )}
              formProps={{ onSubmit: onRecordMovement }}
            >
              <Stack spacing={2}>
                <TextField
                  label={t("dateLabel")}
                  type="date"
                  value={mvDate}
                  slotProps={{ htmlInput: { max: today }, inputLabel: { shrink: true } }}
                  onChange={(e) => setMvDate(e.target.value)}
                />
                <TextField
                  select
                  label={t("typeLabel")}
                  value={mvType}
                  slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
                  onChange={(e) => setMvType(e.target.value)}
                >
                  <option value="Cull">{flockMovementLabel("Cull")}</option>
                  <option value="Adjustment">{flockMovementLabel("Adjustment")}</option>
                </TextField>
                {/* A cull removes at least one bird; an Adjustment counts both
                    ways (added or lost), so its floor is unbounded (#250). */}
                <div className="numfield-field">
                  <label htmlFor={idFor("mv-birds")}>{t("birdsLabel")}</label>
                  <NumberField id={idFor("mv-birds")} label={t("birdsLabel").toLowerCase()}
                    value={mvQty} onChange={setMvQty}
                    min={mvType === "Cull" ? 1 : Number.NEGATIVE_INFINITY} />
                </div>
                <TextField
                  label={t("noteLabel")}
                  value={mvNote}
                  slotProps={{ htmlInput: { maxLength: 500 } }}
                  onChange={(e) => setMvNote(e.target.value)}
                />
                <DialogError errors={errors} scope="record-movement" />
              </Stack>
            </Dialog>
      )}
    </>
  );
}
