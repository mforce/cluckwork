import { useState } from "react";
import type { ReactNode } from "react";
import { Alert, Box } from "@mui/material";

// Copy and download feedback, announced in one live region.
export function useCopy() {
  const [note, setNote] = useState<{ text: string; ok: boolean } | null>(null);
  // Started inside the promise: a plain-http origin has no clipboard at all.
  const copy = (text: string, done: string, failed: string) =>
    Promise.resolve().then(() => navigator.clipboard.writeText(text))
      .then(() => setNote({ text: done, ok: true }), () => setNote({ text: failed, ok: false }));
  const notice: ReactNode = (
    <Box role="status" sx={{ "&:empty": { display: "none" } }}>
      {note && <Alert severity={note.ok ? "success" : "warning"} role="none">{note.text}</Alert>}
    </Box>
  );
  return { copy, notice, setNote };
}
