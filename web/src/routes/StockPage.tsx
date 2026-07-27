import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { getStock, listEggLotMovements, listEggLots } from "../api/cluckwork";
import type { EggLotRow, EggMovementRow, StockRow } from "../api/cluckwork";
import i18n from "../i18n";
import { stockMovementLabel } from "../i18n/enums";

// F2 (#22): current sellable stock by grade; withdrawal-restricted quantities
// are shown separately — they exist but cannot be sold yet.
// #101: each grade expands into its lots, each lot into its movement ledger —
// the explicit rows behind every cached balance.
export function StockPage() {
  const { t } = useTranslation("stock");
  const { t: tc } = useTranslation("common");
  const [rows, setRows] = useState<StockRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [openGrade, setOpenGrade] = useState<string | null>(null);
  const [lots, setLots] = useState<EggLotRow[]>([]);
  const [openLot, setOpenLot] = useState<string | null>(null);
  const [movements, setMovements] = useState<EggMovementRow[] | null>(null);

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
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
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
    </section>
  );
}
