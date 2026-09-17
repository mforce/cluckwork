import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, List, ListItem, Stack, Typography } from "@mui/material";
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

// #95 — manual backup (admin). Downloads only; restore is a deployment
// operation (see the backup section in the README), not an app feature.
export function ExportPage() {
  const { t } = useTranslation("export");
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const download = async (
    key: string,
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
      <Typography variant="h2">{t("heading")}</Typography>
      <Typography variant="body2" color="text.secondary">{t("intro")}</Typography>

      {error && <Alert severity="error">{error}</Alert>}

      <Typography variant="h3" sx={{ mt: 3 }}>{t("fullBackupHeading")}</Typography>
      <Stack sx={{ mt: 1, alignItems: "flex-start" }}>
        <button
          disabled={busy !== null}
          onClick={() =>
            void download("all", downloadFullBackup, "cluckwork-backup.zip")
          }
        >
          {busy === "all" ? t("preparingButton") : t("fullBackupButton")}
        </button>
      </Stack>
      <Typography variant="body2" color="text.secondary">{t("fullBackupHint")}</Typography>

      <Typography variant="h3" sx={{ mt: 3 }}>{t("singleDatasetsHeading")}</Typography>
      <List disablePadding>
        {EXPORT_DATASETS.map((d) => (
          <ListItem key={d} disableGutters>
            <button
              className="link"
              disabled={busy !== null}
              onClick={() =>
                void download(d, () => downloadExportCsv(d), `cluckwork-${d}.csv`)
              }
            >
              {busy === d ? t("preparingButton") : t(`dataset.${d}`)}
            </button>
          </ListItem>
        ))}
      </List>
    </section>
  );
}
