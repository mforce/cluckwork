// web/src/components/PanelPager.tsx
import { Box, IconButton, Typography } from "@mui/material";
import { ChevronLeft, ChevronRight } from "lucide-react";

// #915 — paging inside a Dashboard panel, never an inner scroll region: a
// nested scroller fights the page scroll on touch and traps keyboard users.
// Two steps rather than numbered pages, because Recent orders is fetched a
// page at a time and its length is unknown until the last page lands.
export function PanelPager({ label, previousLabel, nextLabel, hasPrevious, hasNext, onPrevious, onNext }: {
  label: string;
  previousLabel: string;
  nextLabel: string;
  hasPrevious: boolean;
  hasNext: boolean;
  onPrevious: () => void;
  onNext: () => void;
}) {
  return (
    <Box sx={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 1, mt: 1 }}>
      <Typography variant="caption" color="text.secondary">{label}</Typography>
      <Box sx={{ display: "flex", flexShrink: 0 }}>
        <IconButton aria-label={previousLabel} disabled={!hasPrevious} onClick={onPrevious} sx={PAGER_BUTTON_SX}>
          <ChevronLeft size={18} aria-hidden focusable={false} />
        </IconButton>
        <IconButton aria-label={nextLabel} disabled={!hasNext} onClick={onNext} sx={PAGER_BUTTON_SX}>
          <ChevronRight size={18} aria-hidden focusable={false} />
        </IconButton>
      </Box>
    </Box>
  );
}

// The 44px phone target floor, at every width so the two never drift apart.
const PAGER_BUTTON_SX = { "&&": { minWidth: 44, minHeight: 44 } };
