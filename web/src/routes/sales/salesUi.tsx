import { Box } from "@mui/material";
import type { SalesOrder } from "../../api/cluckwork";
import type { usePagedList } from "../../components/usePagedList";
import type { DialogAction } from "../../components/useDialogAction";
import { useFormat } from "../../farm/useFormat";
import { statusLabel } from "../../i18n/enums";

export type SalesAction = DialogAction<"create-order" | "record-payment" | "order-panel">;
export type PagedOrders = ReturnType<typeof usePagedList<SalesOrder>>;

export const NOWRAP = { whiteSpace: "nowrap" as const };
export const LINK_ACTION_SX = {
  minWidth: 0, p: 0, fontWeight: 700, borderRadius: 0, color: "var(--link)",
  textDecoration: "underline", textDecorationColor: "var(--rule-strong)", textUnderlineOffset: "3px",
  "&:hover": { textDecoration: "underline", bgcolor: "transparent" },
  // #930 — MUI's own dark-mode action.disabled (rgba(255,255,255,0.3)) clears
  // only ~2.6:1 against --surface/--surface-2, under the 3:1 floor. `opacity:
  // 1` overrides the global `:where(button:disabled) { opacity: .5 }`
  // (styles.css) — left unset, it halves --muted's own contrast to
  // 2.84:1/2.68:1, still under 3:1.
  "&.Mui-disabled": { color: "var(--muted)", opacity: 1 },
};
// Secondary reading of LINK_ACTION_SX for an action that sits beside a
// primary one in the same manifest-row cell (save/cancel) — same text-link
// footprint so the pair fits the cell edit/remove already fit in, muted
// instead of underlined so save still reads as the affirmative action.
export const SECONDARY_ACTION_SX = {
  ...LINK_ACTION_SX, color: "text.secondary", fontWeight: 500, textDecoration: "none",
  "&:hover": { textDecoration: "underline", bgcolor: "transparent" },
};
export const MANIFEST_ACTIONS_SX = {
  whiteSpace: { md: "nowrap" },
  "& [role=status]": { whiteSpace: "normal" },
  "& button": { display: "inline-flex", minHeight: { xs: 44, md: "auto" }, mr: .75 },
};
export const PICKER_SX = { flex: "0 1 15rem", width: "15rem", minWidth: "8rem", maxWidth: "100%" };
// #986's phone chip: 36px, below the 44px floor the rest of the phone UI keeps,
// because three of these have to share one 353px line.
export const CHIP_SX = { borderRadius: "100px", minHeight: 36, fontSize: ".75rem" };
// The settlement rail is a fixed dark panel in BOTH themes (CONSOLE_RAIL_SX
// pins #2c2429/#fff), so the shared row's --ink and --muted resolved against
// the wrong surface in light: 2.75:1 (#988 review r1). Derived from the rail's
// own white, not copied from the dark palette, which #149 multiplies.
export const RAIL_PHONE_LIST_SX = {
  "--surface": "transparent",
  "--ink": "#fff",
  "--muted": "rgba(255, 255, 255, .72)",
  "--rule": "rgba(255, 255, 255, .22)",
};

export function OrderStatus({ status }: { status: SalesOrder["status"] }) {
  return <Box component="span" sx={{ display: "inline-flex", alignItems: "center", gap: "5px", fontWeight: 700, whiteSpace: "nowrap",
    "&::before": { content: "''", width: "6px", height: "6px", borderRadius: "50%",
      bgcolor: status === "Draft" ? "var(--warn)" : status === "Confirmed" ? "var(--success)" : "var(--error)" },
  }}>{statusLabel(status)}</Box>;
}

// #752 — a percent under 0.05 rounds to "0.0" and sits beside a NON-zero
// amount, so the pair contradicts itself. Below the rendering threshold say
// "<0.1" instead. Built from fmt.count rather than a literal so the decimal
// separator stays the locale's — es writes 0,1.
export function useDiscountPercent() {
  const fmt = useFormat();
  return (percent: number) =>
    percent > 0 && percent < 0.05 ? `<${fmt.count(0.1, 1)}` : fmt.count(percent, 1);
}
