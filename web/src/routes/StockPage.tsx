import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import {
  getStock, listEggLotMovements, listEggLots, recordEggLotMovement,
} from "../api/cluckwork";
import type { EggLotRow, EggMovementRow, StockRow } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { NumberField } from "../components/NumberField";
import { usePendingAction } from "../components/usePendingAction";
import i18n from "../i18n";
import { stockMovementLabel } from "../i18n/enums";
import { newId } from "../lib/ids";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// F2 (#22): current sellable stock by grade; withdrawal-restricted quantities
// are shown separately — they exist but cannot be sold yet.
// #101: each grade expands into its lots, each lot into its movement ledger —
// the explicit rows behind every cached balance.
// #406: admins write off lost stock (or apply a recount) per lot — available
// moves, the daily entry's production figures never do.
export function StockPage() {
  const { t } = useTranslation("stock");
  const { t: tc } = useTranslation("common");
  // UI visibility only (#73/#103) — the endpoint re-checks the role.
  const { isAdmin } = useAuth();
  const { busy, isPending, run: runPending } = usePendingAction();
  const [rows, setRows] = useState<StockRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [openGrade, setOpenGrade] = useState<string | null>(null);
  const [lots, setLots] = useState<EggLotRow[]>([]);
  const [openLot, setOpenLot] = useState<string | null>(null);
  const [movements, setMovements] = useState<EggMovementRow[] | null>(null);

  // #406 write-off dialog: the lot being corrected + its form fields.
  const [writeOffLot, setWriteOffLot] = useState<EggLotRow | null>(null);
  const [woType, setWoType] = useState("Discard");
  // Reconciliation only: a recount can find eggs as well as lose them.
  const [woDirection, setWoDirection] = useState("remove");
  const [woQty, setWoQty] = useState(0);
  const [woReason, setWoReason] = useState("");
  const [dialogError, setDialogError] = useState<string | null>(null);

  // Stable idempotency keys per logical mutation, rotated only after the full
  // action (write + refresh) succeeds — same contract as the other screens.
  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = newId();
    keys.current.set(scope, fresh);
    return fresh;
  };
  const clearKey = (scope: string) => keys.current.delete(scope);

  useEffect(() => {
    getStock()
      .then(setRows)
      .catch(() => setError(i18n.t("stock:loadStockFailed")));
  }, []);

  async function toggleGrade(gradeId: string) {
    setOpenLot(null);
    setMovements(null);
    if (openGrade === gradeId) {
      setOpenGrade(null);
      return;
    }
    try {
      setLots(await listEggLots({ gradeId }));
      setOpenGrade(gradeId);
      setError(null);
    } catch {
      setError(i18n.t("stock:loadLotsFailed"));
    }
  }

  async function toggleLot(lotId: string) {
    if (openLot === lotId) {
      setOpenLot(null);
      setMovements(null);
      return;
    }
    try {
      setMovements(await listEggLotMovements(lotId));
      setOpenLot(lotId);
      setError(null);
    } catch {
      setError(i18n.t("stock:loadMovementsFailed"));
    }
  }

  function openWriteOff(lot: EggLotRow) {
    setWoType("Discard");
    setWoDirection("remove");
    setWoQty(0);
    setWoReason("");
    setDialogError(null);
    setWriteOffLot(lot);
  }

  // The signed delta the API receives: only a reconciliation may add back.
  const woDelta = woType === "Reconciliation" && woDirection === "add" ? woQty : -woQty;

  // Everything the write-off changed, refetched together so the by-grade
  // totals, the lot row and an open ledger never disagree with each other.
  async function refreshAfterWriteOff(lot: EggLotRow) {
    setRows(await getStock());
    if (openGrade !== null) setLots(await listEggLots({ gradeId: openGrade }));
    if (openLot === lot.id) setMovements(await listEggLotMovements(lot.id));
  }

  async function onWriteOff(e: FormEvent) {
    e.preventDefault();
    const lot = writeOffLot;
    if (lot === null) return;
    if (woQty <= 0) {
      setDialogError(i18n.t("stock:writeOffQuantityRequired"));
      return;
    }
    if (!woReason.trim()) {
      setDialogError(i18n.t("stock:writeOffReasonRequired"));
      return;
    }
    // The server enforces the same bounds; failing early spares a round trip.
    const result = lot.quantityAvailable + woDelta;
    if (result < 0 || result > lot.quantityProduced) {
      setDialogError(i18n.t("stock:writeOffOutOfRangeMessage", { produced: lot.quantityProduced }));
      return;
    }
    const scope = `write-off:${lot.id}`;
    const outcome = await runPending(scope, async () => {
      setDialogError(null);
      setMessage(null);
      try {
        const res = await recordEggLotMovement(lot.id, {
          movementType: woType, quantityDelta: woDelta, reason: woReason.trim(),
        }, keyFor(scope));
        // The refresh must succeed before the key rotates: if it throws, the
        // key survives and a retry replays the idempotent write.
        await refreshAfterWriteOff(lot);
        clearKey(scope);
        return res;
      } catch (err) {
        setDialogError(errText(err));
        return undefined;
      }
    });
    if (outcome) {
      setMessage(i18n.t("stock:writeOffRecordedMessage", { available: outcome.quantityAvailable }));
      setWriteOffLot(null);
    }
  }

  if (error && rows === null) {
    return <section><h2>{t("title")}</h2><p className="error">{error}</p></section>;
  }
  if (rows === null) return <section><h2>{t("title")}</h2><p className="muted">{tc("loading")}</p></section>;

  const totalAvailable = rows.reduce((a, r) => a + r.available, 0);
  // Largest available across the loaded rows scales every meter fill so the bars
  // read as relative stock. Guard the divide-by-zero when all rows are empty.
  const maxAvailable = rows.reduce((m, r) => Math.max(m, r.available), 0);

  return (
    <section>
      <h2>{t("title")}</h2>
      {error && <p className="error" role="alert">{error}</p>}
      {message && <p className="success" role="status">{message}</p>}
      {rows.length === 0 ? (
        <p className="muted">{t("noStockMessage")}</p>
      ) : (
        <>
          <table className="data">
            <thead>
              <tr><th>{t("gradeHeader")}</th><th>{t("availableHeader")}</th><th>{t("restrictedHeader")}</th><th></th></tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.eggGradeId}>
                  <td>{r.gradeName}</td>
                  <td>
                    {r.available}
                    <div className="meter" aria-hidden="true">
                      <span style={{ width: (maxAvailable > 0 ? (r.available / maxAvailable) * 100 : 0) + "%" }} />
                    </div>
                  </td>
                  <td>{r.restricted > 0 ? <span className="badge badge-warn">{r.restricted}</span> : "—"}</td>
                  <td>
                    <button className="link" onClick={() => void toggleGrade(r.eggGradeId)}>
                      {openGrade === r.eggGradeId ? t("hideLotsButton") : t("lotsButton")}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="muted">{t("totalAvailableMessage", { available: totalAvailable, grades: rows.length })}</p>

          {openGrade !== null && (
            <>
              <h3>{t("lotsHeading")}</h3>
              {lots.length === 0 ? (
                <p className="muted">{t("noLotsMessage")}</p>
              ) : (
                <table className="data">
                  <thead>
                    <tr><th>{t("producedOnHeader")}</th><th>{t("producedHeader")}</th><th>{t("availableHeader")}</th><th></th></tr>
                  </thead>
                  <tbody>
                    {lots.map((l) => (
                      <tr key={l.id}>
                        <td>{l.productionDate}</td>
                        <td>{l.quantityProduced}</td>
                        <td>{l.quantityAvailable}</td>
                        <td>
                          <button className="link" onClick={() => void toggleLot(l.id)}>
                            {openLot === l.id ? t("hideHistoryButton") : t("historyButton")}
                          </button>
                          {isAdmin && (
                            <button className="link" onClick={() => openWriteOff(l)}>
                              {t("writeOffButton")}
                            </button>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
              {/* Why the action is unavailable, in the place it would be. */}
              {!isAdmin && lots.length > 0 && (
                <p className="muted">{t("writeOffNeedsAdminMessage")}</p>
              )}

              {openLot !== null && movements !== null && (
                <>
                  <h4>{t("movementLedgerHeading")}</h4>
                  <p className="muted">
                    {t("movementLedgerIntro")}
                  </p>
                  <table className="data">
                    <thead>
                      <tr><th>{t("ledgerWhenHeader")}</th><th>{t("ledgerTypeHeader")}</th><th>{t("ledgerChangeHeader")}</th><th>{t("ledgerReasonHeader")}</th></tr>
                    </thead>
                    <tbody>
                      {movements.map((m) => (
                        <tr key={m.id}>
                          <td>{m.createdAtUtc.replace("T", " ").slice(0, 19)}</td>
                          <td>{stockMovementLabel(m.movementType)}</td>
                          <td>{m.quantityDelta > 0 ? `+${m.quantityDelta}` : m.quantityDelta}</td>
                          <td>{m.reason ?? "—"}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </>
              )}
            </>
          )}
        </>
      )}

      {/* #406 — the write-off dialog. Also gated on isAdmin so a mid-session
          demotion can't leave a stale dialog open. */}
      {writeOffLot !== null && (
        <Dialog open={isAdmin} title={t("writeOffDialogTitle", { date: writeOffLot.productionDate })}
          onClose={() => setWriteOffLot(null)}>
          <form className="form-grid" onSubmit={(e) => void onWriteOff(e)}>
            <label>{t("writeOffTypeLabel")}
              <select value={woType} onChange={(e) => setWoType(e.target.value)}>
                <option value="Discard">{stockMovementLabel("Discard")}</option>
                <option value="InternalUse">{stockMovementLabel("InternalUse")}</option>
                <option value="Reconciliation">{stockMovementLabel("Reconciliation")}</option>
              </select>
            </label>
            {woType === "Reconciliation" && (
              <label>{t("writeOffDirectionLabel")}
                <select value={woDirection} onChange={(e) => setWoDirection(e.target.value)}>
                  <option value="remove">{t("writeOffDirectionRemoveOption")}</option>
                  <option value="add">{t("writeOffDirectionAddOption")}</option>
                </select>
              </label>
            )}
            {/* Sibling label, not wrapping — the stepper carries two buttons
                and a <label> may not contain interactive content other than
                its own control (#250). */}
            <div className="numfield-field">
              <label htmlFor="write-off-qty">{t("writeOffQuantityLabel")}</label>
              <NumberField id="write-off-qty" label={t("writeOffQuantityLabel").toLowerCase()}
                value={woQty} onChange={setWoQty} min={0} />
            </div>
            <label>{t("writeOffReasonLabel")}
              <input value={woReason} maxLength={500} required
                onChange={(e) => setWoReason(e.target.value)} />
            </label>
            {woQty > 0 && (
              <p className="muted">
                {t("writeOffPreviewMessage", {
                  current: writeOffLot.quantityAvailable,
                  result: writeOffLot.quantityAvailable + woDelta,
                })}
              </p>
            )}
            {dialogError && <p className="error">{dialogError}</p>}
            <div className="dialog-foot">
              <button type="button" className="link" onClick={() => setWriteOffLot(null)}>{tc("cancel")}</button>
              <BusyButton type="submit" busy={isPending(`write-off:${writeOffLot.id}`)} disabled={busy}>
                {t("writeOffSubmitButton")}
              </BusyButton>
            </div>
          </form>
        </Dialog>
      )}
    </section>
  );
}
