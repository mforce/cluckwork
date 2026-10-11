import { useTranslation } from "react-i18next";
import { useFormat } from "../farm/useFormat";
import { Box, Button, Tooltip, Typography } from "@mui/material";
import { Plug } from "lucide-react";
import type { AuditEvent } from "../api/cluckwork";
import { auditActionLabel, discountReasonLabel, entityTypeLabel } from "../i18n/enums";

// The audit row renderer and its Details line, moved out of AuditPage (#1215).
// It renders exactly where it did before: one <details> per loaded row, with
// AuditDetails inside it. All state stays on the page; `showOnlyApp` is the
// page's own callback (it writes the page's knownAppNames module cache), and
// `appClientIdFilter` is the URL pivot the row's Show-only button compares
// against. Everything rendered comes from the row's own payload; a fetch in
// here is a defect, not an optimisation.

// #745 — the Details line. Renders the sales-line audit payloads as the summary
// the #722 artboard draws, and falls back to the row's reason.
// #756 — also renders the discount reason on a SalesOrder.Confirm row, when the
// order was confirmed below list; absent on an order with no discount, and on
// every row confirmed before this payload shipped (no backfill).
//
// Everything it renders comes from the row's OWN payload. It never resolves
// productId against the catalogue: that would print a renamed product's CURRENT
// name on a historical row, which is exactly what #747's productName snapshot
// exists to prevent. A fetch in here is a defect, not an optimisation.
//
// Money goes through useFormat().money, which is farm-locale bound (§4.5/#650).
// formatMoney keyed off i18n.language is what formattingIndependence.test.ts
// exists to catch.
function AuditDetails({ event }: { event: AuditEvent }) {
  const { t } = useTranslation("audit");
  const fmt = useFormat();

  const summary = (() => {
    if (!event.detailsJson) return null;
    let d: Record<string, unknown>;
    try {
      d = JSON.parse(event.detailsJson) as Record<string, unknown>;
    } catch {
      // A payload we cannot parse is not an error worth showing a user: fall
      // through to the reason, exactly as a row with no payload does.
      return null;
    }

    // #756 — the discount reason that rode into the confirm audit row
    // (ConfirmSaleHandler). Handled BEFORE the productName/currency guard
    // below: a confirm payload carries neither field, so a branch placed
    // after that guard would silently never render (the trap this comment
    // exists to name).
    if (event.action === "SalesOrder.Confirm") {
      const reasonCode = typeof d.discountReasonCode === "string" ? d.discountReasonCode : null;
      if (reasonCode === null) return null;
      const note = typeof d.discountReasonNote === "string" ? d.discountReasonNote : null;
      const reason = discountReasonLabel(reasonCode);
      return note
        ? t("detailsDiscountReasonWithNote", { reason, note })
        : t("detailsDiscountReason", { reason });
    }

    const name = typeof d.productName === "string" ? d.productName : null;
    const code = typeof d.currencyCode === "string" ? d.currencyCode : null;
    const minor = typeof d.currencyMinorUnit === "number" ? d.currencyMinorUnit : null;
    if (name === null || code === null || minor === null) return null;
    const money = (v: number) => fmt.money(v, code, minor);

    // listUnitPriceMinorUnits is nullable and listPriceBasis says why (#720).
    // A null must never render as a zero price.
    const list = typeof d.listUnitPriceMinorUnits === "number" ? d.listUnitPriceMinorUnits : null;

    if (event.action === "SalesOrder.AddItem") {
      const qty = typeof d.quantity === "number" ? d.quantity : null;
      const unit = typeof d.unitPriceMinorUnits === "number" ? d.unitPriceMinorUnits : null;
      if (qty === null || unit === null) return null;
      // #758 — the price shown is ALWAYS `unit`, the price the line sold for.
      // `list` only ever qualifies it. Rendering money(list) here (what #745
      // shipped) makes a line sold at $0.20 against a $0.45 list read "at list
      // $0.45": a plausible, correctly formatted, wrong number that no existing
      // test contradicted.
      //
      // An off-list line gets both numbers and NO marker word, in either
      // direction. #719's open question 4 deliberately treats a markup as not a
      // discount, so this cell states the two facts and lets the reader see the
      // direction rather than editorialising about it.
      return (
        <>
          {name} ×{qty}{" "}
          {list === null ? t("detailsAtPrice", { amount: money(unit) })
           : unit === list ? t("detailsAtList", { amount: money(list) })
           : `${t("detailsAtPrice", { amount: money(unit) })} ${t("detailsListParen", { amount: money(list) })}`}
        </>
      );
    }

    if (event.action === "SalesOrder.UpdateItem") {
      const before = (d.before as { unitPriceMinorUnits?: unknown } | undefined)?.unitPriceMinorUnits;
      const after = (d.after as { unitPriceMinorUnits?: unknown } | undefined)?.unitPriceMinorUnits;
      if (typeof before !== "number" || typeof after !== "number") return null;
      return (
        <>
          {name} <s>{money(before)}</s> → <strong>{money(after)}</strong>{" "}
          {list === null ? t("detailsNoListPrice") : t("detailsListParen", { amount: money(list) })}
        </>
      );
    }

    return null;
  })();

  const details = summary ?? event.reason;
  if (details === null) return null;
  return <Typography component="p" variant="body2">{details}</Typography>;
}

export function AuditEventRow({
  event: e,
  appClientIdFilter,
  showOnlyApp,
}: {
  event: AuditEvent;
  appClientIdFilter: string;
  showOnlyApp: (clientId: string, name: string | null) => void;
}) {
  const { t } = useTranslation("audit");
  const timestamp = e.occurredAtUtc.replace("T", " ").slice(0, 19);
  const action = auditActionLabel(e.action);
  const summaryId = `audit-event-${e.id}`;
  const actorId = `audit-event-actor-${e.id}`;
  const app = e.connectedAppClientId === null
    ? null
    : { clientId: e.connectedAppClientId, name: e.connectedAppName ?? t("unnamedConnectedApp") };
  return (
    // The Tooltip trigger is the leaf span inside `<summary>`, not
    // `<details>`: `<details>` already declares its own
    // `aria-describedby={actorId}` (the article's actor
    // description), and JSX prop merge lets the outer element's
    // own props win, so a Tooltip on `<details>` would silently
    // drop the JSON description instead of combining with it.
    // `|| undefined` (not `??`) also omits the attribute for an
    // empty string, not only for null.
    //
    // #800 — the actor is the summary's second line, so the
    // article is named by the first line alone. The plug is
    // decorative: the "via" text already says it.
    <details
      key={e.id}
      className="audit-event"
      role="article"
      aria-labelledby={summaryId}
      aria-describedby={actorId}
    >
      <summary>
        <Tooltip title={e.detailsJson || undefined} describeChild>
          <Box component="span" id={summaryId}>{timestamp} UTC · {action}</Box>
        </Tooltip>
        <span className="audit-event-actor" id={actorId}>
          <strong>{e.actorEmail}</strong>
          {app && (
            <>
              {" "}
              <span className="audit-event-app">
                <Plug className="audit-event-app-icon" size={14} aria-hidden="true" />
                {t("viaConnectedApp", { app: app.name })}
              </span>
            </>
          )}
        </span>
      </summary>
      <div className="audit-event-body">
        <Typography component="p" variant="body2">{action}</Typography>
        <Typography component="p" variant="body2">
          {entityTypeLabel(e.entityType)} {e.entityId.slice(0, 8)}
        </Typography>
        <AuditDetails event={e} />
        {app && app.clientId !== appClientIdFilter && (
          <Box>
            <Button variant="outlined" size="small" onClick={() => showOnlyApp(app.clientId, e.connectedAppName)}>
              {e.connectedAppName === null
                ? t("showOnlyThisAppButton")
                : t("showOnlyAppButton", { app: e.connectedAppName })}
            </Button>
          </Box>
        )}
      </div>
    </details>
  );
}
