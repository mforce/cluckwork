import type { Dispatch, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { Box, Stack, Typography } from "@mui/material";
import { BRANDS } from "../../lib/brand";
import type { Brand } from "../../lib/brand";
import type { en } from "../../i18n/en";

// The curated palettes' raw ids stay lowercase (#149) — they are matched by
// exact-match CSS selectors and written into data-brand, so they are DATA, not
// copy. Only the DISPLAY name is translatable, so it lives in the `settings`
// catalog (paletteAubergine/paletteForest/…) rather than a hardcoded Record
// here (#182, Task 21). This map is PaletteFieldset-only — there is no other
// screen that renders a palette name — so it stays local instead of growing
// enums.ts into a family with a single consumer. `satisfies Record<Brand,
// SettingsKey>` still gives the same exhaustiveness guarantee enums.ts's
// families use: a BRANDS id with no entry, or an entry pointing at a typo'd
// catalog key, is a compile error.
type SettingsKey = Extract<keyof typeof en.settings, string>;
const PALETTE_LABEL_KEYS = {
  aubergine: "paletteAubergine",
  forest: "paletteForest",
  slate: "paletteSlate",
  terracotta: "paletteTerracotta",
} as const satisfies Record<Brand, SettingsKey>;

// Each swatch previews its OWN palette's fill regardless of which palette is
// currently applied (#149), so these are literal by necessity — not read from
// the live theme, which only ever resolves the ACTIVE brand.
const PALETTE_SWATCH_COLORS = {
  aubergine: "#4a154b",
  forest: "#14432a",
  slate: "#1b3a5c",
  terracotta: "#6b2716",
} as const satisfies Record<Brand, string>;

export function PaletteFieldset({
  brand, displayedBrand, setBrand, saving, stale, savedBrand,
}: {
  brand: Brand;
  displayedBrand: Brand;
  setBrand: Dispatch<SetStateAction<Brand>>;
  saving: boolean;
  stale: boolean;
  savedBrand: Brand | null;
}) {
  const { t } = useTranslation("settings");

  return (
            <Box component="fieldset" sx={{
              border: "1px solid", borderColor: "divider", borderRadius: "var(--r-panel)",
              padding: "1rem", margin: 0, marginTop: "1.25rem",
            }}>
              <Typography component="legend" variant="subtitle2">{t("paletteLegend")}</Typography>
              <Typography variant="body2" color="text.secondary" id="palette-hint">
                {t("paletteHint")}
              </Typography>
              <Stack direction="row" sx={{ flexWrap: "wrap", gap: "0.75rem", mt: 1 }} aria-describedby="palette-hint">
                {BRANDS.map((id) => (
                  <Box component="label" key={id} sx={{
                    position: "relative",
                    display: "inline-flex", alignItems: "center", gap: "0.5rem", padding: "6px 10px",
                    border: "1px solid", borderColor: brand === id ? "info.main" : "divider",
                    borderRadius: "4px", cursor: saving ? "default" : "pointer",
                    opacity: saving ? 0.6 : 1,
                    boxShadow: brand === id ? (theme) => `inset 0 0 0 1px ${theme.palette.info.main}` : "none",
                    "&:has(input:focus-visible)": {
                      outline: "2px solid", outlineColor: "info.main", outlineOffset: "2px",
                    },
                  }}>
                    <input
                      className="sr-only"
                      type="radio"
                      name="brand"
                      value={id}
                      checked={displayedBrand === id}
                      onChange={() => setBrand(id)}
                      disabled={saving || stale}
                    />
                    <Box aria-hidden sx={{
                      width: 14, height: 14, borderRadius: "var(--r-pill)", border: "1px solid",
                      borderColor: "divider", backgroundColor: PALETTE_SWATCH_COLORS[id],
                    }} />
                    <Typography component="span" variant="body2">{t(PALETTE_LABEL_KEYS[id])}</Typography>
                  </Box>
                ))}
              </Stack>
              {!stale && savedBrand !== null && brand !== savedBrand && (
                <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }} role="status">
                  {t("palettePreviewHint", { save: t("saveButton") })}
                </Typography>
              )}
            </Box>
  );
}
