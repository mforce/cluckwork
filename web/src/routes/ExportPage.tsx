import { useState } from "react";
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
      <h2>Export</h2>
      <p className="muted">
        Download your account&apos;s data as CSV files — a manual backup you
        can keep anywhere. Money values are exported in minor units (cents)
        with their currency, exactly as stored.
      </p>

      {error && <p className="error" role="alert">{error}</p>}

      <h3>Full backup</h3>
      <p>
        <button
          disabled={busy !== null}
          onClick={() =>
            void download("all", downloadFullBackup, "cluckwork-backup.zip")
          }
        >
          {busy === "all" ? "Preparing…" : "Download full backup (zip)"}
        </button>
      </p>
      <p className="muted">
        One zip with every dataset below plus a manifest of row counts.
      </p>

      <h3>Single datasets</h3>
      <ul className="export-list">
        {EXPORT_DATASETS.map((d) => (
          <li key={d}>
            <button
              className="link"
              disabled={busy !== null}
              onClick={() =>
                void download(d, () => downloadExportCsv(d), `cluckwork-${d}.csv`)
              }
            >
              {busy === d ? "Preparing…" : d}
            </button>
          </li>
        ))}
      </ul>
    </section>
  );
}
