import { useMemo } from "react";
import { Box } from "@mui/material";
import { encode } from "uqr";

// #1274 decision 21 — exactly level M, as the worst-case URI was measured
// against (G2.6), never boosted, and the four-module quiet zone the QR
// specification asks for.
export const QR_OPTIONS = { ecc: "M", boostEcc: false, border: 4 } as const;

// Drawn here from the module matrix, so the key never leaves the page: no
// image endpoint, and no data: URI, which the CSP forbids.
export function TwoFactorQr({ uri, label }: { uri: string; label: string }) {
  const { size, path } = useMemo(() => {
    const { size, data } = encode(uri, QR_OPTIONS);
    return { size, path: data.flatMap((row, y) => row.map((dark, x) => (dark ? `M${x} ${y}h1v1h-1z` : ""))).join("") };
  }, [uri]);
  // Dark on light in both themes, and three CSS pixels a module: at about two,
  // jsQR could not read a version 18 code from a 1x screenshot (#1277 proof).
  return (
    <Box component="svg" role="img" aria-label={label} viewBox={`0 0 ${size} ${size}`} shapeRendering="crispEdges"
      sx={{ display: "block", width: size * 3, maxWidth: "100%", height: "auto", flexShrink: 0, bgcolor: "common.white", color: "common.black", borderRadius: 1 }}>
      <path d={path} fill="currentColor" />
    </Box>
  );
}
