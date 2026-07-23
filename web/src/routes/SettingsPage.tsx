import { useEffect, useRef, useState } from "react";
import type { ChangeEvent, FormEvent } from "react";
import { Trash2, Upload } from "lucide-react";
import {
  LOGO_ACCEPT, LOGO_MAX_BYTES, getFarmSettings, removeFarmLogo, updateFarmSettings,
  uploadFarmLogo,
} from "../api/cluckwork";
import type { FarmSettings } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useConfirm } from "../components/useConfirm";
import { useFarm } from "../farm/useFarm";
import { useLogoObjectUrl } from "../farm/useLogoObjectUrl";
import { newId } from "../lib/ids";

// Mirrors the server's validators (UpdateFarmSettingsValidator + Account) so a
// too-long value is refused by the field rather than by a 400.
const MAX_NAME = 120;
const MAX_LOCALE = 32;
const MAX_TIMEZONE = 64;
const MAX_FORMAT = 32;

const UNIT_SYSTEMS = ["Metric", "Imperial"];
const WEEKDAYS = [
  "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday",
];

// The browser's own IANA list, so the field offers exactly the zones this
// browser can also FORMAT with — the same table todayIso() reads. The server
// keeps its own (newer or older) list and remains the authority; a zone typed
// by hand is still accepted if it validates there.
const TIME_ZONES = Intl.supportedValuesOf("timeZone");

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// An optional field: blank means "no override", which the API expects as null
// rather than "".
const orNull = (value: string): string | null => (value.trim() === "" ? null : value.trim());

// #123 — farm settings (admin). The §4.5 localization set plus the logo, with
// §4.6's currency lock surfaced as a disabled field instead of a 422 the user
// only meets after typing.
export function SettingsPage() {
  const { refresh } = useFarm();
  const { confirm, confirmDialog } = useConfirm();

  const [loaded, setLoaded] = useState<FarmSettings | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [name, setName] = useState("");
  const [timeZoneId, setTimeZoneId] = useState("");
  const [locale, setLocale] = useState("");
  const [currencyCode, setCurrencyCode] = useState("");
  const [unitSystem, setUnitSystem] = useState("Metric");
  const [firstDayOfWeek, setFirstDayOfWeek] = useState("");
  const [dateFormat, setDateFormat] = useState("");
  const [timeFormat, setTimeFormat] = useState("");

  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const saveKey = useRef<string>(newId());

  const [logoBusy, setLogoBusy] = useState(false);
  const [logoError, setLogoError] = useState<string | null>(null);
  const logoKey = useRef<string>(newId());

  const logoHash = loaded?.settings.logoContentHash ?? null;
  const hasLogo = logoHash !== null;
  const logoUrl = useLogoObjectUrl(logoHash);

  // Re-reads after every write: the save bumps the farm's version, and both
  // logo writes change the content hash the preview keys off.
  async function load() {
    const next = await getFarmSettings();
    setLoaded(next);
    const s = next.settings;
    setName(s.name);
    setTimeZoneId(s.timeZoneId);
    setLocale(s.locale);
    setCurrencyCode(s.currencyCode);
    setUnitSystem(s.unitSystem);
    setFirstDayOfWeek(s.firstDayOfWeek ?? "");
    setDateFormat(s.dateFormatOverride ?? "");
    setTimeFormat(s.timeFormatOverride ?? "");
  }

  // Once, on mount. `load` is re-created every render but must not re-run on
  // one: it overwrites the fields, so a reload mid-edit would discard whatever
  // the user had typed.
  useEffect(() => {
    load().catch(() => setLoadError("Could not load farm settings."));
  }, []);

  async function onSave(e: FormEvent) {
    e.preventDefault();
    if (saving || loaded === null) return;
    setSaving(true);
    setSaveError(null);
    setSaved(false);
    try {
      await updateFarmSettings({
        name: name.trim(),
        timeZoneId: timeZoneId.trim(),
        locale: locale.trim(),
        currencyCode: currencyCode.trim().toUpperCase(),
        unitSystem,
        firstDayOfWeek: orNull(firstDayOfWeek),
        dateFormatOverride: orNull(dateFormat),
        timeFormatOverride: orNull(timeFormat),
        version: loaded.settings.version,
      }, saveKey.current);
      // A fresh key: reusing it would make the NEXT save replay this response
      // instead of writing.
      saveKey.current = newId();
      await load();
      // The chrome and every date input read the farm from context — this is
      // what makes the change show without a reload (§4.5).
      await refresh();
      setSaved(true);
    } catch (err) {
      setSaveError(
        err instanceof ApiError && err.status === 409
          ? "Someone else changed these settings while this screen was open. Reload and try again."
          : errText(err));
    } finally {
      setSaving(false);
    }
  }

  async function onPickLogo(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    // Re-selecting the same file has to fire change again — otherwise a failed
    // upload could not be retried without picking something else first.
    e.target.value = "";
    if (file === undefined) return;

    setLogoError(null);
    // The server refuses this too (413). Checking here spares a megabyte on
    // the wire and gives the size back in the message.
    if (file.size > LOGO_MAX_BYTES) {
      setLogoError(`That image is ${Math.ceil(file.size / 1024)} KB. The limit is 1024 KB.`);
      return;
    }

    setLogoBusy(true);
    try {
      await uploadFarmLogo(file, logoKey.current);
      logoKey.current = newId();
      await load();
      await refresh();
    } catch (err) {
      setLogoError(errText(err));
    } finally {
      setLogoBusy(false);
    }
  }

  async function onRemoveLogo() {
    const ok = await confirm({
      title: "Remove the farm logo?",
      body: "The sidebar goes back to the Cluckwork mark. You can upload another at any time.",
      confirmLabel: "Remove logo",
      destructive: true,
    });
    if (!ok) return;

    setLogoError(null);
    setLogoBusy(true);
    try {
      await removeFarmLogo(logoKey.current);
      logoKey.current = newId();
      await load();
      await refresh();
    } catch (err) {
      setLogoError(errText(err));
    } finally {
      setLogoBusy(false);
    }
  }

  if (loadError !== null) return (
    <section>
      <h2>Farm settings</h2>
      <p className="error" role="alert">{loadError}</p>
    </section>
  );

  if (loaded === null) return (
    <section>
      <h2>Farm settings</h2>
      <p className="muted">Loading…</p>
    </section>
  );

  return (
    <section>
      <h2>Farm settings</h2>
      <p className="muted">
        How this farm names itself, and the locale, timezone and currency every
        date and amount in the app is shown in.
      </p>

      <h3>Logo</h3>
      <div className="logo-panel">
        {logoUrl === null ? (
          <p className="muted logo-empty">No logo set — the sidebar shows the Cluckwork mark.</p>
        ) : (
          <img className="logo-preview" src={logoUrl} alt="Current farm logo" />
        )}
        <div className="logo-actions">
          {/* A real labelled file input rather than a button driving a hidden
              one: the picker is the control, and wrapping it in its own label
              keeps it reachable by keyboard and by name. */}
          <label className="logo-file">
            <Upload size={16} aria-hidden /> {hasLogo ? "Replace the logo" : "Upload a logo"}
            <input type="file" accept={LOGO_ACCEPT} disabled={logoBusy}
              onChange={(e) => void onPickLogo(e)} />
          </label>
          {hasLogo && (
            <button type="button" className="btn-danger" disabled={logoBusy}
              onClick={() => void onRemoveLogo()}>
              <Trash2 size={16} aria-hidden /> Remove
            </button>
          )}
        </div>
      </div>
      <p className="muted">
        PNG, JPEG or WebP, up to 1 MB and 4096&nbsp;px a side. The image is
        stored re-written: camera and location metadata are stripped, and
        animation is not kept.
      </p>
      {logoError !== null && <p className="error" role="alert">{logoError}</p>}

      <h3>Localization</h3>
      <form className="form-grid" onSubmit={(e) => void onSave(e)}>
        <label>Farm name
          <input value={name} required maxLength={MAX_NAME}
            onChange={(e) => setName(e.target.value)} />
        </label>

        <label>Timezone
          <input list="tz-options" value={timeZoneId} required maxLength={MAX_TIMEZONE}
            onChange={(e) => setTimeZoneId(e.target.value)} />
          <datalist id="tz-options">
            {TIME_ZONES.map((tz) => <option key={tz} value={tz} />)}
          </datalist>
        </label>

        <label>Locale
          <input value={locale} required maxLength={MAX_LOCALE} placeholder="en-US"
            onChange={(e) => setLocale(e.target.value)} />
        </label>

        <label>Currency
          <input value={currencyCode} required maxLength={3}
            disabled={!loaded.canChangeCurrency}
            onChange={(e) => setCurrencyCode(e.target.value.toUpperCase())} />
        </label>

        <label>Unit system
          <select value={unitSystem} onChange={(e) => setUnitSystem(e.target.value)}>
            {UNIT_SYSTEMS.map((u) => <option key={u} value={u}>{u}</option>)}
          </select>
        </label>

        <label>First day of week
          <select value={firstDayOfWeek} onChange={(e) => setFirstDayOfWeek(e.target.value)}>
            <option value="">Follow the locale</option>
            {WEEKDAYS.map((d) => <option key={d} value={d}>{d}</option>)}
          </select>
        </label>

        <label>Date format
          <input value={dateFormat} maxLength={MAX_FORMAT} placeholder="Follow the locale"
            onChange={(e) => setDateFormat(e.target.value)} />
        </label>

        <label>Time format
          <input value={timeFormat} maxLength={MAX_FORMAT} placeholder="Follow the locale"
            onChange={(e) => setTimeFormat(e.target.value)} />
        </label>

        <div className="actions">
          <button type="submit" disabled={saving}>
            {saving ? "Saving…" : "Save settings"}
          </button>
        </div>
      </form>

      {/* §4.6: the rule, where the field it disables is, rather than as a 422
          after the user has typed a new code. */}
      {!loaded.canChangeCurrency && (
        <p className="warn">
          The currency is fixed at {loaded.settings.currencyCode}: this farm has
          already recorded amounts in it. Money that has been recorded is never
          re-denominated, so changing it would leave every stored total meaning
          something else.
        </p>
      )}
      {saveError !== null && <p className="error" role="alert">{saveError}</p>}
      {saved && <p className="success" role="status">Settings saved.</p>}

      {confirmDialog}
    </section>
  );
}
