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

export function ExportPage() {
  const { t } = useTranslation("export");
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
      <Typography variant="h1">{t("heading")}</Typography>
      <Typography variant="body2" color="text.secondary">{t("intro")}</Typography>
      <Divider sx={{ my: 2 }} />

      {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}

      <Paper
        variant="outlined"
        sx={{ bgcolor: "var(--surface-2)", borderRadius: "var(--r-panel)", p: 3, mb: 3 }}
      >
        <Typography variant="h2">{t("fullBackupHeading")}</Typography>
        <Typography variant="body2" sx={{ mb: 2 }}>{t("fullBackupHint")}</Typography>
        <Button
          variant="contained"
          disabled={busy !== null}
          sx={{ width: { xs: "100%", md: "auto" } }}
          onClick={() =>
            void download("all", downloadFullBackup, "cluckwork-backup.zip")
          }
        >
          {busy === "all" ? t("preparingButton") : t("fullBackupButton")}
        </Button>
      </Paper>

      <Typography variant="h2">{t("singleDatasetsHeading")}</Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>{t("datasetHint")}</Typography>
      <Stack spacing={2} sx={{ alignItems: "flex-start", width: "100%" }}>
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
