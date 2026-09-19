import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Box, Button, Divider, Paper, Stack, TextField, Typography } from "@mui/material";
import {
  EXPORT_DATASETS,
  downloadExportCsv,
  downloadFullBackup,
} from "../api/cluckwork";
import { ApiError } from "../api/client";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

function saveBlob(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

// #95/#833 — manual backup (admin). Downloads only; restore is a deployment
// operation (see the backup section in the README), not an app feature.
// D&D "Focus panels" (Concept C, docs/designs/674-tail-redesign): an action
// catalog, not a form — the full backup is the one panel on the screen
// (nothing to configure, one press), and single datasets are a select plus
// one download button rather than one button per dataset.
export function ExportPage() {
  const { t } = useTranslation("export");
  // "all" while the full backup is in flight, "csv" while the selected
  // dataset is — both disable the whole screen so a second download cannot
  // start, same guarantee the per-dataset-button version had.
  const [busy, setBusy] = useState<"all" | "csv" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dataset, setDataset] = useState<string>(EXPORT_DATASETS[0]);

  const download = async (
    key: "all" | "csv",
    fetcher: () => Promise<{ blob: Blob; filename: string | null }>,
    fallbackName: string,
  ) => {
    setBusy(key);
    setError(null);
    try {
      const { blob, filename } = await fetcher();
      saveBlob(blob, filename ?? fallbackName);
    } catch (err) {
      setError(errText(err));
    } finally {
      setBusy(null);
    }
  };

  return (
    <section>
      <Typography variant="overline" color="text.secondary" component="p" sx={{ m: 0 }}>
        {t("eyebrow")}
      </Typography>
      <Typography variant="h2">{t("heading")}</Typography>
      <Typography variant="body2" color="text.secondary">{t("intro")}</Typography>
      <Divider sx={{ my: 2 }} />

      {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}

      <Paper
        variant="outlined"
        sx={{ bgcolor: "var(--surface-2)", borderRadius: "var(--r-panel)", p: 3, mb: 3 }}
      >
        <Typography variant="h3">{t("fullBackupHeading")}</Typography>
        <Typography variant="body2" sx={{ mb: 2 }}>{t("fullBackupHint")}</Typography>
        <Button
          variant="contained"
          disabled={busy !== null}
          onClick={() =>
            void download("all", downloadFullBackup, "cluckwork-backup.zip")
          }
        >
          {busy === "all" ? t("preparingButton") : t("fullBackupButton")}
        </Button>
      </Paper>

      <Typography variant="h3">{t("singleDatasetsHeading")}</Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>{t("datasetHint")}</Typography>
      <Stack spacing={2} sx={{ alignItems: "flex-start", maxWidth: "24rem" }}>
        <TextField
          select
          fullWidth
          label={t("datasetLabel")}
          value={dataset}
          disabled={busy !== null}
          onChange={(e) => setDataset(e.target.value)}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
        >
          {EXPORT_DATASETS.map((d) => <option key={d} value={d}>{t(`dataset.${d}`)}</option>)}
        </TextField>
        <Box>
          <Button
            variant="contained"
            disabled={busy !== null}
            onClick={() =>
              void download("csv", () => downloadExportCsv(dataset), `cluckwork-${dataset}.csv`)
            }
          >
            {busy === "csv" ? t("preparingButton") : t("downloadCsvButton")}
          </Button>
        </Box>
      </Stack>
    </section>
  );
}
