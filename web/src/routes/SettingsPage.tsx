import { useEffect, useId, useRef, useState } from "react";
import type { ChangeEvent, FormEvent } from "react";
import { Trash2, Upload } from "lucide-react";
import {
  LOGO_ACCEPT, getFarmSettings, removeFarmLogo, updateFarmSettings,
  uploadFarmLogo,
} from "../api/cluckwork";
import type { FarmSettings, UpdateFarmSettings } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useConfirm } from "../components/useConfirm";
import { useFarm } from "../farm/useFarm";
import { useLogoObjectUrl } from "../farm/useLogoObjectUrl";
import { isKnownTimeZone } from "../lib/dates";
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
//
// Guarded because this runs at MODULE LOAD, and App.tsx imports this screen
// statically: on a browser without Intl.supportedValuesOf the throw would
// happen before React mounts, outside every ErrorBoundary, and white-screen the
// whole app rather than degrade one Setup screen (review of #123). An empty
// list leaves the field a plain text input, which the server validates anyway.
const TIME_ZONES: string[] = (() => {
  try {
    return Intl.supportedValuesOf("timeZone");
  } catch {
    return [];
  }
})();

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// An optional field: blank means "no override", which the API expects as null
// rather than "".
const orNull = (value: string): string | null => (value.trim() === "" ? null : value.trim());

// An idempotency key and the exact payload it was minted for.
//
// A key identifies ONE attempt at ONE write. Rotating only on success is right
// for retrying the same thing after an ambiguous failure — the server replays
// instead of writing twice — but wrong the moment the payload changes: upload
// logo-v1, lose the response after the server committed it, pick logo-v2
// instead, and the same key replays v1's stored 200. The upload reports success
// and v1 stays (review of #123). Binding the key to the payload keeps the
// retry-dedupe and drops the wrong replay.
interface Attempt {
  key: string;
  payload: string;
}

function keyFor(attempt: Attempt | null, payload: string): Attempt {
  return attempt !== null && attempt.payload === payload
    ? attempt
    : { key: newId(), payload };
}

// #123 — farm settings (admin). The §4.5 localization set plus the logo, with
// §4.6's currency lock surfaced as a locked field instead of a 422 the user
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
  // Set when a save landed but the follow-up read did not: the screen still
  // holds the OLD version, so another save from here would 409 and blame
  // someone else for this user's own write.
  const [stale, setStale] = useState(false);
  const saveAttempt = useRef<Attempt | null>(null);

  const [logoBusy, setLogoBusy] = useState(false);
  const [focusUploadAfterRemove, setFocusUploadAfterRemove] = useState(false);
  const [logoError, setLogoError] = useState<string | null>(null);
  const [logoMessage, setLogoMessage] = useState<string | null>(null);
  const uploadAttempt = useRef<Attempt | null>(null);
  const removeAttempt = useRef<Attempt | null>(null);
  const uploadInput = useRef<HTMLInputElement>(null);

  const currencyNoteId = useId();
  const timeZoneNoteId = useId();
  const logoRulesId = useId();

  const logoHash = loaded?.settings.logoContentHash ?? null;
  const hasLogo = logoHash !== null;
  const logo = useLogoObjectUrl(logoHash);

  // Server config, carried on the settings payload — never a client constant,
  // so it cannot drift from what the server enforces (#123).
  const maxUploadBytes = loaded?.logoMaxUploadBytes ?? 0;
  const maxUploadKb = Math.floor(maxUploadBytes / 1024);
  const maxUploadMb = maxUploadBytes / (1024 * 1024);

  const timeZoneUnknown = timeZoneId.trim() !== "" && !isKnownTimeZone(timeZoneId.trim());

  // Seeds every field from the server. Called on mount, and after a save (the
  // version moved, and the currency may have locked). NOT after a logo write:
  // that would overwrite whatever the user had typed but not yet saved, which
  // is exactly what the empty dependency list below exists to prevent (review
  // of #123).
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

  // A logo write changes exactly one thing in this payload — the content hash —
  // and the upload response carries it. No re-read, so nothing the user has
  // typed is disturbed and there is no second in-flight GET to land out of
  // order with the save's.
  function applyLogoHash(contentHash: string | null) {
    setLoaded((prev) => prev === null ? prev : {
      ...prev,
      settings: { ...prev.settings, logoContentHash: contentHash },
    });
  }

  // Once, on mount. `load` is re-created every render but must not re-run on
  // one: it overwrites the fields, so a reload mid-edit would discard whatever
  // the user had typed.
  useEffect(() => {
    load().catch(() => setLoadError("Could not load farm settings."));
  }, []);

  // Focus lands here only once the upload input is enabled again — a disabled
  // control silently refuses focus() and the keyboard would be left on <body>.
  // Verified rather than assumed, for the same reason.
  useEffect(() => {
    if (!focusUploadAfterRemove || logoBusy) return;
    setFocusUploadAfterRemove(false);
    uploadInput.current?.focus();
  }, [focusUploadAfterRemove, logoBusy]);

  async function onSave(e: FormEvent) {
    e.preventDefault();
    if (saving || logoBusy || stale || loaded === null) return;
    setSaving(true);
    setSaveError(null);
    setSaved(false);

    const body: UpdateFarmSettings = {
      name: name.trim(),
      timeZoneId: timeZoneId.trim(),
      locale: locale.trim(),
      currencyCode: currencyCode.trim().toUpperCase(),
      unitSystem,
      firstDayOfWeek: orNull(firstDayOfWeek),
      dateFormatOverride: orNull(dateFormat),
      timeFormatOverride: orNull(timeFormat),
      version: loaded.settings.version,
    };
    const attempt = keyFor(saveAttempt.current, JSON.stringify(body));
    saveAttempt.current = attempt;

    try {
      await updateFarmSettings(body, attempt.key);
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        // The version this screen holds is now definitively wrong, and a retry
        // sends the same one: the middleware caches only 2xx, so it re-executes
        // and 409s again, forever. Disable the button so it agrees with the
        // message rather than inviting the loop (pi round 2).
        setStale(true);
        setSaveError(
          "Someone else changed these settings while this screen was open. Reload and try again.");
      } else {
        setSaveError(errText(err));
      }
      setSaving(false);
      return;
    }

    // Written. Anything that fails from here is a REFRESH failure, and saying
    // "could not save" about a save that landed is how a user ends up making
    // the same change twice.
    saveAttempt.current = null;
    setSaved(true);
    try {
      await load();
    } catch {
      setStale(true);
      setSaveError(
        "Saved. This screen could not read the settings back — reload the page before saving again.");
      setSaving(false);
      return;
    }

    // The chrome and every date input read the farm from context — this is what
    // makes the change show without a reload (§4.5). It cannot throw (the
    // provider has to survive a failed read), so it REPORTS: relying on a throw
    // meant a failed /account left the save looking fully applied while the
    // shell still held the old timezone (codex round 2).
    const refreshed = await refresh();
    if (!refreshed)
      setSaveError(
        "Saved. The rest of the app could not pick the change up — reload the page to be sure it is applied everywhere.");
    setSaving(false);
  }

  async function onPickLogo(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    // Re-selecting the same file has to fire change again — otherwise a failed
    // upload could not be retried without picking something else first.
    e.target.value = "";
    if (file === undefined) return;

    if (saving || logoBusy) return;
    setLogoError(null);
    setLogoMessage(null);
    // The server refuses this too (413). Checking here spares the upload on
    // the wire and gives the size back in the message. The limit is the server's
    // own, fetched with the settings.
    if (file.size > maxUploadBytes) {
      setLogoError(`That image is ${Math.ceil(file.size / 1024)} KB. The limit is ${maxUploadKb} KB.`);
      return;
    }

    // Identity, not contents: two different files are two different writes and
    // must not share a key.
    const attempt = keyFor(uploadAttempt.current, `${file.name}:${file.size}:${file.lastModified}`);
    uploadAttempt.current = attempt;

    setLogoBusy(true);
    try {
      const stored = await uploadFarmLogo(file, attempt.key);
      uploadAttempt.current = null;
      applyLogoHash(stored.contentHash);
      setLogoMessage("Logo updated.");
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
    if (saving || logoBusy) return;

    setLogoError(null);
    setLogoMessage(null);
    // Bound to the hash being removed, not a bare "remove": after an ambiguous
    // removal of H1 another admin can upload H2, and a retry on a shared key
    // would replay H1's 204 — reporting success while H2 quietly survives
    // (codex round 2).
    const attempt = keyFor(removeAttempt.current, `remove:${logoHash ?? ""}`);
    removeAttempt.current = attempt;

    setLogoBusy(true);
    try {
      await removeFarmLogo(attempt.key);
      removeAttempt.current = null;
      applyLogoHash(null);
      setLogoMessage("Logo removed.");
      // Deferred to an effect rather than called here. The Remove button that
      // was just clicked unmounts with the logo and Dialog only restores focus
      // to a trigger still in the document, so focus would land on <body> — but
      // calling focus() at this point does nothing either, because the upload
      // input is still `disabled={logoBusy}` and focus() on a disabled control
      // is a no-op (the same hazard Dialog.tsx documents). It has to happen
      // after the busy state clears (round 2: two agents).
      setFocusUploadAfterRemove(true);
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
        How this farm names itself, and the locale, timezone and currency it
        records and reads its work in.
      </p>

      <h3>Logo</h3>
      <div className="logo-panel">
        {logo.url !== null ? (
          <img className="logo-preview" src={logo.url} alt="Current farm logo" />
        ) : (
          // Three different reasons there is no image on screen, and only one
          // of them is "no logo set" — saying that while a Remove button sits
          // beside it is a contradiction the reader cannot resolve.
          <p className="muted logo-empty">
            {logo.loading ? "Loading the logo…"
              : logo.failed ? "The logo could not be loaded."
                : "No logo set — the sidebar shows the Cluckwork mark."}
          </p>
        )}
        <div className="logo-actions">
          {/* A real labelled file input rather than a button driving a hidden
              one: the picker is the control, and wrapping it in its own label
              keeps it reachable by keyboard and by name. */}
          <label className="logo-file">
            <Upload size={16} aria-hidden /> {hasLogo ? "Replace the logo" : "Upload a logo"}
            <input ref={uploadInput} type="file" accept={LOGO_ACCEPT} disabled={logoBusy || saving}
              aria-describedby={logoRulesId}
              onChange={(e) => void onPickLogo(e)} />
          </label>
          {hasLogo && (
            <button type="button" className="btn-danger" disabled={logoBusy || saving}
              onClick={() => void onRemoveLogo()}>
              <Trash2 size={16} aria-hidden /> Remove
            </button>
          )}
        </div>
      </div>
      <p className="muted" id={logoRulesId}>
        PNG, JPEG or WebP, up to {maxUploadMb} MB and 4096&nbsp;px a side. Animated images
        are not accepted. The image is stored re-written, with camera and
        location metadata removed.
      </p>
      {/* Both the upload and the removal are silent otherwise — the only signal
          is an image appearing, which a screen reader does not see. */}
      <p className="success" role="status">
        {logoBusy ? "Working…" : logoMessage ?? ""}
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
            aria-describedby={timeZoneUnknown ? timeZoneNoteId : undefined}
            onChange={(e) => setTimeZoneId(e.target.value)} />
          <datalist id="tz-options">
            {TIME_ZONES.map((tz) => <option key={tz} value={tz} />)}
          </datalist>
        </label>
        {/* Outside the <label>, deliberately: a note nested in one becomes part
            of the field's accessible NAME, so the control would announce itself
            as "Currency Fixed at USD this farm has already…". aria-describedby
            is how a note reaches a control without renaming it. */}
        {timeZoneUnknown && (
          <p className="warn field-note" id={timeZoneNoteId}>
            {/* The server validates against ITS tzdata, which can be newer than
                this browser's. A zone it accepts but the browser cannot format
                saves fine and then quietly sends every date field back to the
                device's day — the one thing this whole slice removes. */}
            This browser does not know that timezone, so dates here would follow
            the device instead of the farm. Pick one from the list.
          </p>
        )}

        <label>Locale
          <input value={locale} required maxLength={MAX_LOCALE} placeholder="en-US"
            onChange={(e) => setLocale(e.target.value)} />
        </label>

        <label>Currency
          {/* readOnly, not disabled: a disabled input leaves the tab order, so
              a keyboard user never reaches the field OR the reason it is
              locked. Read-only keeps both, and aria-describedby carries the
              reason with the control. */}
          <input value={currencyCode} required maxLength={3}
            className={loaded.canChangeCurrency ? undefined : "locked"}
            readOnly={!loaded.canChangeCurrency}
            aria-describedby={loaded.canChangeCurrency ? undefined : currencyNoteId}
            onChange={(e) => setCurrencyCode(e.target.value.toUpperCase())} />
        </label>
        {/* §4.6: the rule, at the field it locks, rather than as a 422 after
            the user has typed a new code. */}
        {!loaded.canChangeCurrency && (
          <p className="warn field-note" id={currencyNoteId}>
            The currency is fixed at {loaded.settings.currencyCode}: this farm
            has already recorded amounts in it. Recorded money is never
            re-priced, so changing this would leave every stored total meaning
            something else.
          </p>
        )}

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
          {/* Disabled while a logo write is in flight too: the save issues its
              own GET, and a delayed one landing after the logo write would
              restore the hash the logo write had just replaced — the very
              stale-response class removing load() from the logo path was meant
              to close (codex round 2). */}
          <button type="submit" disabled={saving || logoBusy || stale}>
            {saving ? "Saving…" : "Save settings"}
          </button>
        </div>
      </form>

      {/* What actually acts on a save today. The timezone reaches every date
          field immediately (#123); the rest are stored on the farm and take
          effect as the screens that would render through them adopt them (#45
          carries the display formatting). Saying "everywhere, straight away"
          would be a promise the app does not keep. */}
      <p className="muted">
        The timezone applies everywhere as soon as it is saved. Locale, unit
        system and the format overrides are recorded against the farm and will
        drive how amounts, dates and measurements are displayed once that
        formatting lands.
      </p>

      {saveError !== null && <p className="error" role="alert">{saveError}</p>}
      {/* Always mounted, like the logo's — a live region inserted at the same
          moment as its text is not reliably announced, and the logo panel two
          sections up already says so. */}
      <p className="success" role="status">
        {saved && saveError === null ? "Settings saved." : ""}
      </p>

      {confirmDialog}
    </section>
  );
}
