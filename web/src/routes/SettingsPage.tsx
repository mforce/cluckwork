import { useEffect, useId, useRef, useState } from "react";
import type { ChangeEvent, FormEvent } from "react";
import { Trans, useTranslation } from "react-i18next";
import { ChevronDown, Trash2, Upload } from "lucide-react";
import {
  Accordion, AccordionDetails, AccordionSummary, Alert, Box, Paper, Stack, TextField, Typography,
} from "@mui/material";
import {
  BANNER_ACCEPT, LOGO_ACCEPT, getFarmBanner, getFarmSettings, listEggUnitConversions,
  removeFarmBanner, removeFarmLogo, updateFarmSettings, uploadFarmBanner, uploadFarmLogo,
} from "../api/cluckwork";
import type { EggUnitConversion, FarmSettings, UpdateFarmSettings } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { BusyButton } from "../components/BusyButton";
import { useConfirm } from "../components/useConfirm";
import { usePendingAction } from "../components/usePendingAction";
import { useFarm } from "../farm/useFarm";
import { useBannerObjectUrl, useLogoObjectUrl } from "../farm/useLogoObjectUrl";
import { farmBindingToken, getBoundFarmCode } from "../auth/tokenStore";
import { cacheBannerBytes, forgetBannerFor } from "../lib/bannerCache";
import { BRANDS, DEFAULT_BRAND, applyBrand, isBrand } from "../lib/brand";
import type { Brand } from "../lib/brand";
import { isKnownTimeZone } from "../lib/dates";
import { newId } from "../lib/ids";
import i18n from "../i18n";
import {
  unitSystemLabel, weekdayLabel, workerSaleAllocationPolicyLabel,
  WORKER_SALE_ALLOCATION_POLICY_VALUES,
} from "../i18n/enums";
import type { en } from "../i18n/en";

// The curated palettes' raw ids stay lowercase (#149) — they are matched by
// exact-match CSS selectors and written into data-brand, so they are DATA, not
// copy. Only the DISPLAY name is translatable, so it lives in the `settings`
// catalog (paletteAubergine/paletteForest/…) rather than a hardcoded Record
// here (#182, Task 21). This map is SettingsPage-only — there is no other
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

// #452 — common date/time format presets, offered as a dropdown so most
// people never need to know .NET custom-format-string syntax (dd vs MM vs
// yyyy, which one is the day and which is the month). "Custom…" reveals the
// pre-existing free-text field for anything not listed — the server
// validates whatever ends up there either way
// (UpdateFarmSettingsValidator.BeAUsableFormat), this dropdown only changes
// how most people arrive at a value, not what's accepted.
const DATE_FORMAT_PRESETS = ["MM/dd/yyyy", "dd/MM/yyyy", "yyyy-MM-dd"];
const TIME_FORMAT_PRESETS = ["h:mm tt", "HH:mm"];
const CUSTOM_FORMAT_OPTION = "__custom__";

// A stored value that isn't blank and isn't one of the offered presets means
// the farm is already using a custom format — the dropdown must open on
// "Custom…" (with the real value in the revealed field) rather than
// silently falling back to "Follow the locale" and discarding it on save.
const isCustomFormat = (value: string, presets: string[]) =>
  value !== "" && !presets.includes(value);

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

// #727 — the same contract for the ceiling, in numbers. The blank test comes
// FIRST and is the whole point: Number("") is 0, so testing the parsed value
// would send "no discount allowed" for a farm that meant "no limit". The range
// is held by the input's own min/max/step and by the server's validator; this
// only decides blank from not-blank.
const percentOrNull = (value: string): number | null =>
  (value.trim() === "" ? null : Number(value.trim()));

// A byte cap as a short human string (#123). The cap is admin CONFIG, so it is
// not always a round power of two: 2 MB reads "2 MB", 512 KB reads "512 KB",
// and 1,000,000 bytes reads "977 KB" rather than the raw "0.95367… MB" a plain
// division would print (codex review). MB is used at or above 1 MiB, trimmed to
// at most one decimal; KB below it.
export function formatByteCap(bytes: number): string {
  const mib = 1024 * 1024;
  if (bytes >= mib) {
    const mb = bytes / mib;
    // A whole number of MB drops the decimal; otherwise one place is enough for
    // a size limit and never runs off into binary-fraction noise.
    return `${Number.isInteger(mb) ? mb : mb.toFixed(1)} MB`;
  }
  return `${Math.floor(bytes / 1024)} KB`;
}

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

// #123/#729 — farm settings (Owner-only). The §4.5 localization set plus the logo, with
// §4.6's currency lock surfaced as a locked field instead of a 422 the user
// only meets after typing.
export function SettingsPage() {
  const { refresh } = useFarm();
  const { confirm, confirmDialog } = useConfirm();
  const { t } = useTranslation("settings");
  const { t: tc } = useTranslation("common");

  const [loaded, setLoaded] = useState<FarmSettings | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [name, setName] = useState("");
  const [timeZoneId, setTimeZoneId] = useState("");
  const [locale, setLocale] = useState("");
  const [currencyCode, setCurrencyCode] = useState("");
  const [unitSystem, setUnitSystem] = useState("Metric");
  const [brand, setBrand] = useState<string>(DEFAULT_BRAND);
  // #444 — the farm-default Daily Entry stepper pack unit. Fetched alongside
  // the settings themselves (not a separate effect) so the select's value and
  // its options always land together — no race where the stored code briefly
  // has no matching <option>.
  const [defaultStepperUnit, setDefaultStepperUnit] = useState("Individual");
  const [stepperUnits, setStepperUnits] = useState<EggUnitConversion[]>([]);
  // #612 — how a restricted plain Worker's sale confirmation may draw stock.
  // Lives on the FarmSettings wrapper (admin-only), not on Account/settings —
  // every other role only ever sees the derived showFarmWideSaleAllocationNotice.
  const [workerSaleAllocationPolicy, setWorkerSaleAllocationPolicy] =
    useState("AssignedFlocksOnly");
  // #727 — held as the raw input string, not a number, because blank and 0 are
  // two different settings and a number state would have to spell blank as
  // null anyway. Lives on the FarmSettings wrapper (admin-only) like the
  // policy above; every other role sees only Account.yourMaxDiscountPercent.
  const [maxDiscountPercent, setMaxDiscountPercent] = useState("");
  const [firstDayOfWeek, setFirstDayOfWeek] = useState("");
  const [dateFormat, setDateFormat] = useState("");
  // #452 — true once the user (or the loaded value) is on the "Custom…"
  // branch of the dropdown, revealing the free-text field below it. Kept
  // separate from `dateFormat` itself: deriving this purely from the value
  // would collapse straight back to "Follow the locale" the instant someone
  // picks "Custom…" from an empty/follow-locale start, since the value
  // hasn't changed yet — the field would never actually appear.
  const [dateFormatCustom, setDateFormatCustom] = useState(false);
  const [timeFormat, setTimeFormat] = useState("");
  const [timeFormatCustom, setTimeFormatCustom] = useState(false);

  // #236 — ONE flight for the whole screen (the old `saving` + `logoBusy`
  // pair, consolidated). The cross-checks those two states enforced — no save
  // during a logo write and vice versa — are now the hook's single-flight
  // guarantee; the derived names below keep each surface bound to ITS scope
  // only (palette radios and the Save label to "settings", the logo status
  // region and the focus-after-remove effect to the logo scopes).
  const { busy, isPending, run } = usePendingAction();
  const saving = isPending("settings");
  const logoBusy = isPending("logo:upload") || isPending("logo:remove");
  const bannerBusy = isPending("banner:upload") || isPending("banner:remove");

  const [saveError, setSaveError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  // Set when a save landed but the follow-up read did not: the screen still
  // holds the OLD version, so another save from here would 409 and blame
  // someone else for this user's own write.
  const [stale, setStale] = useState(false);
  const saveAttempt = useRef<Attempt | null>(null);

  const [focusUploadAfterRemove, setFocusUploadAfterRemove] = useState(false);
  const [logoError, setLogoError] = useState<string | null>(null);
  const [logoMessage, setLogoMessage] = useState<string | null>(null);
  const uploadAttempt = useRef<Attempt | null>(null);
  const removeAttempt = useRef<Attempt | null>(null);
  const uploadInput = useRef<HTMLInputElement>(null);

  const [focusBannerUploadAfterRemove, setFocusBannerUploadAfterRemove] = useState(false);
  const [bannerError, setBannerError] = useState<string | null>(null);
  const [bannerMessage, setBannerMessage] = useState<string | null>(null);
  const bannerUploadAttempt = useRef<Attempt | null>(null);
  const bannerRemoveAttempt = useRef<Attempt | null>(null);
  const bannerUploadInput = useRef<HTMLInputElement>(null);
  // Codex review round 4 — farmBindingToken() alone can't detect a LATER
  // banner operation on the SAME farm (upload then remove, or two uploads
  // back to back): it only changes on a farm/account rebind, and the
  // post-upload re-fetch below is fire-and-forget. Bumped by every
  // banner-mutating operation before it starts and captured going into the
  // re-fetch; the fetch's cache write is skipped once a later operation has
  // moved the counter on, so a slow re-fetch can never resurrect or
  // overwrite what a newer operation already did.
  const bannerOpGeneration = useRef(0);

  const currencyNoteId = useId();
  const timeZoneNoteId = useId();
  const logoRulesId = useId();
  const bannerRulesId = useId();

  const logoHash = loaded?.settings.logoContentHash ?? null;
  const hasLogo = logoHash !== null;
  const logo = useLogoObjectUrl(logoHash);

  const bannerHash = loaded?.settings.bannerContentHash ?? null;
  const hasBanner = bannerHash !== null;
  const banner = useBannerObjectUrl(bannerHash);

  // Server config, carried on the settings payload — never a client constant,
  // so it cannot drift from what the server enforces (#123, #179).
  const maxUploadBytes = loaded?.logoMaxUploadBytes ?? 0;
  const maxUploadKb = Math.floor(maxUploadBytes / 1024);
  const bannerMaxUploadBytes = loaded?.bannerMaxUploadBytes ?? 0;
  const bannerMaxUploadKb = Math.floor(bannerMaxUploadBytes / 1024);

  const timeZoneUnknown = timeZoneId.trim() !== "" && !isKnownTimeZone(timeZoneId.trim());

  // Seeds every field from the server. Called on mount, and after a save (the
  // version moved, and the currency may have locked). NOT after a logo write:
  // that would overwrite whatever the user had typed but not yet saved, which
  // is exactly what the empty dependency list below exists to prevent (review
  // of #123).
  async function load() {
    const [next, units] = await Promise.all([getFarmSettings(), listEggUnitConversions()]);
    setLoaded(next);
    setStepperUnits(units);
    const s = next.settings;
    setName(s.name);
    setTimeZoneId(s.timeZoneId);
    setLocale(s.locale);
    setCurrencyCode(s.currencyCode);
    setUnitSystem(s.unitSystem);
    setFirstDayOfWeek(s.firstDayOfWeek ?? "");
    const nextDateFormat = s.dateFormatOverride ?? "";
    setDateFormat(nextDateFormat);
    setDateFormatCustom(isCustomFormat(nextDateFormat, DATE_FORMAT_PRESETS));
    // A palette can be retired while farms still reference it. Echoing the
    // stored id straight back on the next save would 422; showing the default
    // selected means the next save writes a curated value, which is the
    // recovery the design describes.
    setBrand(isBrand(s.brand) ? s.brand : DEFAULT_BRAND);
    const nextTimeFormat = s.timeFormatOverride ?? "";
    setTimeFormat(nextTimeFormat);
    setTimeFormatCustom(isCustomFormat(nextTimeFormat, TIME_FORMAT_PRESETS));
    // Same recovery as brand: a unit can be deactivated (Products screen)
    // while still named as the farm default. Individual is always active and
    // cannot be deactivated (EggUnitConversion.cs), so it is always a safe
    // fallback that the next save writes back as a valid value.
    const activeCodes = units.filter((u) => u.active).map((u) => u.unitCode);
    setDefaultStepperUnit(activeCodes.includes(s.defaultStepperUnit) ? s.defaultStepperUnit : "Individual");
    setWorkerSaleAllocationPolicy(next.workerSaleAllocationPolicy);
    // String(0) is "0", not "", so a farm that allows no discount at all loads
    // its 0 back into the field instead of reading as "no limit".
    setMaxDiscountPercent(next.maxDiscountPercent === null ? "" : String(next.maxDiscountPercent));
    return next;
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

  // Same reasoning as applyLogoHash, for the banner (#179) — the two are
  // independent, so a banner write must never touch logoContentHash or vice
  // versa.
  function applyBannerHash(contentHash: string | null) {
    setLoaded((prev) => prev === null ? prev : {
      ...prev,
      settings: { ...prev.settings, bannerContentHash: contentHash },
    });
  }

  // Once, on mount. `load` is re-created every render but must not re-run on
  // one: it overwrites the fields, so a reload mid-edit would discard whatever
  // the user had typed.
  useEffect(() => {
    load().catch(() => setLoadError(i18n.t("settings:loadFailedMessage")));
  }, []);

  // Focus lands here only once the upload input is enabled again — a disabled
  // control silently refuses focus() and the keyboard would be left on <body>.
  // Verified rather than assumed, for the same reason.
  useEffect(() => {
    if (!focusUploadAfterRemove || logoBusy) return;
    setFocusUploadAfterRemove(false);
    uploadInput.current?.focus();
  }, [focusUploadAfterRemove, logoBusy]);

  useEffect(() => {
    if (!focusBannerUploadAfterRemove || bannerBusy) return;
    setFocusBannerUploadAfterRemove(false);
    bannerUploadInput.current?.focus();
  }, [focusBannerUploadAfterRemove, bannerBusy]);

  async function onSave(e: FormEvent) {
    e.preventDefault();
    // In-flight re-entry (the old `saving || logoBusy` check) is the hook's
    // job now: run() below skips while any flight is open.
    if (stale || loaded === null) return;
    await run("settings", async () => {
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
        brand,
        defaultStepperUnit,
        workerSaleAllocationPolicy,
        maxDiscountPercent: percentOrNull(maxDiscountPercent),
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
          setSaveError(i18n.t("settings:versionConflictMessage"));
        } else {
          setSaveError(errText(err));
        }
        return;
      }

      // Written. Anything that fails from here is a REFRESH failure, and saying
      // "could not save" about a save that landed is how a user ends up making
      // the same change twice.
      saveAttempt.current = null;
      setSaved(true);
      try {
        // Captured BEFORE the await: the response may land after a farm switch
        // in the same tab, in which case it is farm A's value, not this farm's.
        const boundAt = farmBindingToken();
        const fresh = await load();
        // Applied from THIS response rather than waiting on refresh() below:
        // refresh() cannot throw (the provider has to survive a failed read), so
        // a successful save with a failed refresh would otherwise leave the old
        // palette live and cached while the authoritative value was in hand (#149).
        applyBrand(fresh.settings.brand, boundAt);
      } catch {
        setStale(true);
        setSaveError(i18n.t("settings:saveReadBackFailedMessage"));
        return;
      }

      // The chrome and every date input read the farm from context — this is what
      // makes the change show without a reload (§4.5). It cannot throw (the
      // provider has to survive a failed read), so it REPORTS: relying on a throw
      // meant a failed /account left the save looking fully applied while the
      // shell still held the old timezone (codex round 2).
      const refreshed = await refresh();
      if (!refreshed)
        setSaveError(i18n.t("settings:refreshFailedMessage"));
    });
  }

  async function onPickLogo(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    // Re-selecting the same file has to fire change again — otherwise a failed
    // upload could not be retried without picking something else first.
    e.target.value = "";
    if (file === undefined) return;

    if (busy) return;
    setLogoError(null);
    setLogoMessage(null);
    // The server refuses this too (413). Checking here spares the upload on
    // the wire and gives the size back in the message. The limit is the server's
    // own, fetched with the settings. Refused BEFORE any flight opens — a local
    // size check is not "Working…".
    if (file.size > maxUploadBytes) {
      setLogoError(i18n.t("settings:logoOversizeMessage", {
        actualKb: Math.ceil(file.size / 1024),
        limitKb: maxUploadKb,
      }));
      return;
    }

    // Identity, not contents: two different files are two different writes and
    // must not share a key.
    const attempt = keyFor(uploadAttempt.current, `${file.name}:${file.size}:${file.lastModified}`);
    uploadAttempt.current = attempt;

    await run("logo:upload", async () => {
      try {
        const stored = await uploadFarmLogo(file, attempt.key);
        uploadAttempt.current = null;
        applyLogoHash(stored.contentHash);
        setLogoMessage(i18n.t("settings:logoUpdatedMessage"));
        await refresh();
      } catch (err) {
        setLogoError(errText(err));
      }
    });
  }

  async function onRemoveLogo() {
    const ok = await confirm({
      title: i18n.t("settings:removeLogoConfirmTitle"),
      body: i18n.t("settings:removeLogoConfirmBody"),
      confirmLabel: i18n.t("settings:removeLogoConfirmLabel"),
      destructive: true,
    });
    if (!ok) return;
    if (busy) return;

    setLogoError(null);
    setLogoMessage(null);
    // Bound to the hash being removed, not a bare "remove": after an ambiguous
    // removal of H1 another admin can upload H2, and a retry on a shared key
    // would replay H1's 204 — reporting success while H2 quietly survives
    // (codex round 2).
    const attempt = keyFor(removeAttempt.current, `remove:${logoHash ?? ""}`);
    removeAttempt.current = attempt;

    await run("logo:remove", async () => {
      try {
        await removeFarmLogo(attempt.key);
        removeAttempt.current = null;
        applyLogoHash(null);
        setLogoMessage(i18n.t("settings:logoRemovedMessage"));
        // Deferred to an effect rather than called here. The Remove button that
        // was just clicked unmounts with the logo and Dialog only restores focus
        // to a trigger still in the document, so focus would land on <body> — but
        // calling focus() at this point does nothing either, because the upload
        // input is still disabled while the logo flight is open, and focus() on
        // a disabled control is a no-op (the same hazard Dialog.tsx documents).
        // It has to happen after the busy state clears (round 2: two agents).
        setFocusUploadAfterRemove(true);
        await refresh();
      } catch (err) {
        setLogoError(errText(err));
      }
    });
  }

  async function onPickBanner(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (file === undefined) return;

    if (busy) return;
    setBannerError(null);
    setBannerMessage(null);
    if (file.size > bannerMaxUploadBytes) {
      setBannerError(i18n.t("settings:bannerOversizeMessage", {
        actualKb: Math.ceil(file.size / 1024),
        limitKb: bannerMaxUploadKb,
      }));
      return;
    }

    const attempt = keyFor(bannerUploadAttempt.current, `${file.name}:${file.size}:${file.lastModified}`);
    bannerUploadAttempt.current = attempt;
    const generation = ++bannerOpGeneration.current;

    await run("banner:upload", async () => {
      try {
        const stored = await uploadFarmBanner(file, attempt.key);
        bannerUploadAttempt.current = null;
        applyBannerHash(stored.contentHash);
        setBannerMessage(i18n.t("settings:bannerUpdatedMessage"));
        // #833 finding 4, corrected by Codex finding 1 — cache the server's
        // SANITIZED bytes, not the raw File: the server strips EXIF and can
        // re-encode on upload, so caching the File directly could show Login
        // metadata or an orientation the server already removed. getFarmBanner
        // is the same authenticated read BrandSplash's post-login fetch uses.
        // Fire-and-forget: a fetch/cache failure here costs one stale pre-auth
        // image at most, never surfaced as a Settings error. Guarded by
        // bannerOpGeneration (Codex review round 4) so a later operation —
        // another upload, or a remove — can't be overwritten or resurrected
        // by this fetch landing after it.
        const tokenAt = farmBindingToken();
        void getFarmBanner()
          .then(({ blob }) => {
            if (bannerOpGeneration.current !== generation) return;
            return cacheBannerBytes(blob, tokenAt);
          })
          .catch(() => {});
        await refresh();
      } catch (err) {
        setBannerError(errText(err));
      }
    });
  }

  async function onRemoveBanner() {
    const ok = await confirm({
      title: i18n.t("settings:removeBannerConfirmTitle"),
      body: i18n.t("settings:removeBannerConfirmBody"),
      confirmLabel: i18n.t("settings:removeBannerConfirmLabel"),
      destructive: true,
    });
    if (!ok) return;
    if (busy) return;

    setBannerError(null);
    setBannerMessage(null);
    const attempt = keyFor(bannerRemoveAttempt.current, `remove:${bannerHash ?? ""}`);
    bannerRemoveAttempt.current = attempt;
    // Invalidates a still-in-flight upload's post-upload re-fetch (Codex
    // review round 4) — without this, that fetch resolving AFTER this
    // remove completes would re-cache the just-removed banner's bytes.
    bannerOpGeneration.current += 1;

    await run("banner:remove", async () => {
      try {
        await removeFarmBanner(attempt.key);
        bannerRemoveAttempt.current = null;
        applyBannerHash(null);
        setBannerMessage(i18n.t("settings:bannerRemovedMessage"));
        // #833 finding 4 — otherwise a removed banner stays showable on
        // Login (from the pre-login cache) until "Forget this farm" is
        // used, well after the server itself has forgotten it.
        const slug = getBoundFarmCode();
        if (slug !== null) forgetBannerFor(slug);
        setFocusBannerUploadAfterRemove(true);
        await refresh();
      } catch (err) {
        setBannerError(errText(err));
      }
    });
  }

  if (loadError !== null) return (
    <section>
      <Typography variant="h2">{t("heading")}</Typography>
      <Alert severity="error">{loadError}</Alert>
    </section>
  );

  if (loaded === null) return (
    <section>
      <Typography variant="h2">{t("heading")}</Typography>
      <Typography variant="body2" color="text.secondary">{tc("loading")}</Typography>
    </section>
  );

  // The pill-shaped label-wrapping-file-input control (#236): it must stay a
  // real <label> around a real <input type="file"> for keyboard/AT reasons,
  // so it cannot become a BusyButton or an MUI Button — only its look moves
  // to sx. Shared by the logo and banner pickers.
  const fileButtonSx = {
    // flexDirection is explicit, not left to inline-flex's row default: this
    // renders a real <label>, and styles.css's bare-element `:where(label)`
    // rule sets `flex-direction: column` with zero specificity — the ONLY
    // declaration for that property unless sx names one too, so it wins by
    // default and stacked the icon above the text (same trap
    // FarmThemeProvider.tsx's MuiFormControlLabel comment already names).
    display: "inline-flex", flexDirection: "row", alignItems: "center", gap: "0.4rem",
    cursor: "pointer", fontWeight: 600, fontSize: "0.92rem", color: "primary.contrastText",
    backgroundColor: "primary.main", borderRadius: "var(--r-pill)", padding: "0.6rem 1.15rem",
    "&:hover": { backgroundColor: "primary.dark" },
    "&:has(input:disabled)": { opacity: 0.55, cursor: "default" },
    "&:focus-within": { outline: "2px solid", outlineColor: "primary.main", outlineOffset: "2px" },
    "& input[type='file']": {
      position: "absolute", width: "1px", height: "1px", opacity: 0, pointerEvents: "none",
    },
  } as const;
  const previewImgSx = {
    height: 72, maxWidth: 240, objectFit: "contain" as const, backgroundColor: "#fff",
    border: "1px solid var(--hairline)", borderRadius: "var(--r-panel)", padding: "0.5rem",
  };

  return (
    <section>
      <Typography variant="overline" color="text.secondary" component="p" sx={{ m: 0 }}>
        {t("eyebrow")}
      </Typography>
      <Typography variant="h2">{t("heading")}</Typography>
      <Typography variant="body2" color="text.secondary">
        {t("intro")}
      </Typography>

      {/* Logo and banner writes remain independent actions even though the
          four settings groups share this form. */}
      <Stack component="form" spacing={2} sx={{ mt: 3, pb: "6rem" }} onSubmit={(e) => void onSave(e)}>
        <Accordion defaultExpanded disableGutters>
          <AccordionSummary expandIcon={<ChevronDown size={18} aria-hidden />}>
            <Typography variant="h3" component="span">{t("identityImagesHeading")}</Typography>
          </AccordionSummary>
          <AccordionDetails>
            <Stack direction={{ xs: "column", md: "row" }} spacing={2.5}>
              <Paper variant="outlined" sx={{ flex: 1, borderRadius: "var(--r-panel)", p: 2.5 }}>
                <Typography variant="subtitle1" sx={{ fontWeight: 700 }}>{t("logoSectionHeading")}</Typography>
                <Stack direction="row" sx={{ alignItems: "center", gap: "1.25rem", flexWrap: "wrap", my: 1.5 }}>
                  {logo.url !== null ? (
                    <Box component="img" src={logo.url} alt={t("logoAlt")} sx={previewImgSx} />
                  ) : (
                    // Three different reasons there is no image on screen, and
                    // only one of them is "no logo set" — saying that while a
                    // Remove button sits beside it is a contradiction the
                    // reader cannot resolve.
                    <Typography variant="body2" color="text.secondary" sx={{ m: 0 }}>
                      {logo.loading ? t("logoLoadingMessage")
                        : logo.failed ? t("logoLoadFailedMessage")
                          : t("logoNoneMessage")}
                    </Typography>
                  )}
                </Stack>
                <Stack direction="row" sx={{ gap: "0.75rem", flexWrap: "wrap", mb: 1.5 }}>
                  {/* A real labelled file input rather than a button driving a
                      hidden one: the picker is the control, and wrapping it in
                      its own label keeps it reachable by keyboard and by name. */}
                  {/* Carve-out (#236): a labelled file input is not a button,
                      so it cannot be a BusyButton — it keeps the plain disable
                      and the existing logo status region below carries the
                      announcement. */}
                  <Box component="label" sx={fileButtonSx}>
                    <Upload size={16} aria-hidden /> {hasLogo ? t("replaceLogoButton") : t("uploadLogoButton")}
                    <input ref={uploadInput} type="file" accept={LOGO_ACCEPT} disabled={busy}
                      aria-describedby={logoRulesId}
                      onChange={(e) => void onPickLogo(e)} />
                  </Box>
                  {hasLogo && (
                    <BusyButton type="button" className="btn-danger" disabled={busy}
                      busy={isPending("logo:remove")}
                      onClick={() => void onRemoveLogo()}>
                      <Trash2 size={16} aria-hidden /> {t("removeLogoButton")}
                    </BusyButton>
                  )}
                </Stack>
                <Box component="details" sx={{ color: "text.secondary", mb: 1 }}>
                  <Typography component="summary" variant="body2" sx={{ color: "text.primary", cursor: "pointer" }}>
                    {t("imageGuidanceHeading")}
                  </Typography>
                  <Box sx={{ pt: 1 }}>
                    <Typography variant="body2" color="text.secondary" id={logoRulesId}>
                      {t("logoRulesHint", { cap: formatByteCap(maxUploadBytes) })}
                    </Typography>
                    <Typography variant="body2" color="text.secondary">
                      <Trans ns="settings" i18nKey="logoSquareHint" components={{ strong: <strong /> }} />
                    </Typography>
                  </Box>
                </Box>
                {/* The upload is silent otherwise — a file input cannot be a
                    BusyButton, so this region carries its "Working…". The
                    removal is deliberately NOT announced here: the Remove
                    BusyButton's own live region already says it, and both
                    speaking would double the announcement (#242). Results
                    (logoMessage) still land here for both writes. Always
                    mounted, empty or not: a live region inserted at the same
                    moment as its text is not reliably announced. */}
                <Typography id="logo-status" variant="body2" role="status" color="success.main">
                  {isPending("logo:upload") ? t("logoWorkingMessage") : logoMessage ?? ""}
                </Typography>
                {logoError !== null && <Alert severity="error">{logoError}</Alert>}
              </Paper>

              <Paper variant="outlined" sx={{ flex: 1, borderRadius: "var(--r-panel)", p: 2.5 }}>
                <Typography variant="subtitle1" sx={{ fontWeight: 700 }}>{t("bannerSectionHeading")}</Typography>
                <Stack direction="row" sx={{ alignItems: "center", gap: "1.25rem", flexWrap: "wrap", my: 1.5 }}>
                  {banner.url !== null ? (
                    <Box component="img" src={banner.url} alt={t("bannerAlt")}
                      sx={{ ...previewImgSx, maxWidth: "100%", width: 360, maxHeight: 160 }} />
                  ) : (
                    <Typography variant="body2" color="text.secondary" sx={{ m: 0 }}>
                      {banner.loading ? t("bannerLoadingMessage")
                        : banner.failed ? t("bannerLoadFailedMessage")
                          : t("bannerNoneMessage")}
                    </Typography>
                  )}
                </Stack>
                <Stack direction="row" sx={{ gap: "0.75rem", flexWrap: "wrap", mb: 1.5 }}>
                  <Box component="label" sx={fileButtonSx}>
                    <Upload size={16} aria-hidden /> {hasBanner ? t("replaceBannerButton") : t("uploadBannerButton")}
                    <input ref={bannerUploadInput} type="file" accept={BANNER_ACCEPT} disabled={busy}
                      aria-describedby={bannerRulesId}
                      onChange={(e) => void onPickBanner(e)} />
                  </Box>
                  {hasBanner && (
                    <BusyButton type="button" className="btn-danger" disabled={busy}
                      busy={isPending("banner:remove")}
                      onClick={() => void onRemoveBanner()}>
                      <Trash2 size={16} aria-hidden /> {t("removeBannerButton")}
                    </BusyButton>
                  )}
                </Stack>
                <Box component="details" sx={{ color: "text.secondary", mb: 1 }}>
                  <Typography component="summary" variant="body2" sx={{ color: "text.primary", cursor: "pointer" }}>
                    {t("imageGuidanceHeading")}
                  </Typography>
                  <Typography variant="body2" color="text.secondary" id={bannerRulesId} sx={{ pt: 1 }}>
                    {t("bannerRulesHint", { cap: formatByteCap(bannerMaxUploadBytes) })}
                  </Typography>
                </Box>
                <Typography id="banner-status" variant="body2" role="status" color="success.main">
                  {isPending("banner:upload") ? t("bannerWorkingMessage") : bannerMessage ?? ""}
                </Typography>
                {bannerError !== null && <Alert severity="error">{bannerError}</Alert>}
              </Paper>
            </Stack>

            {/* Farm palette picker (#149): a colour swatch has no MUI control
                of its own (pair 21's reasoning), so the fieldset/legend
                structure stays and only its styling moves to sx. */}
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
                    display: "inline-flex", alignItems: "center", gap: "0.5rem", padding: "0.5rem 0.75rem",
                    border: "1px solid", borderColor: brand === id ? "info.main" : "divider",
                    borderRadius: "var(--r-pill)", cursor: saving ? "default" : "pointer",
                    opacity: saving ? 0.6 : 1,
                    boxShadow: brand === id ? (theme) => `inset 0 0 0 1px ${theme.palette.info.main}` : "none",
                  }}>
                    <input
                      type="radio"
                      name="brand"
                      value={id}
                      checked={brand === id}
                      onChange={() => setBrand(id)}
                      disabled={saving}
                    />
                    {/* The swatch is decorative: the visible name is what names
                        the option, so selection never depends on seeing colour. */}
                    <Box aria-hidden sx={{
                      width: 18, height: 18, borderRadius: "var(--r-pill)", border: "1px solid",
                      borderColor: "divider", backgroundColor: PALETTE_SWATCH_COLORS[id],
                    }} />
                    <Typography component="span" variant="body2">{t(PALETTE_LABEL_KEYS[id])}</Typography>
                  </Box>
                ))}
              </Stack>
            </Box>
          </AccordionDetails>
        </Accordion>

        <Accordion disableGutters slotProps={{ transition: { unmountOnExit: true } }}>
          <AccordionSummary expandIcon={<ChevronDown size={18} aria-hidden />}>
            <Typography variant="h3" component="span">{t("localizationSectionHeading")}</Typography>
          </AccordionSummary>
          <AccordionDetails>
      <Stack spacing={2} sx={{ maxWidth: "40rem" }}>
        <TextField label={t("farmNameLabel")} value={name} required
          onChange={(e) => setName(e.target.value)}
          slotProps={{ htmlInput: { maxLength: MAX_NAME } }} />

        <TextField label={t("timezoneLabel")} value={timeZoneId} required
          onChange={(e) => setTimeZoneId(e.target.value)}
          slotProps={{
            htmlInput: {
              list: "tz-options", maxLength: MAX_TIMEZONE,
              "aria-describedby": timeZoneUnknown ? timeZoneNoteId : undefined,
            },
          }} />
        <datalist id="tz-options">
          {TIME_ZONES.map((tz) => <option key={tz} value={tz} />)}
        </datalist>
        {/* Outside the field, deliberately: a note nested in the label becomes
            part of the control's accessible NAME, so it would announce itself
            as "Currency Fixed at USD this farm has already…". aria-describedby
            is how a note reaches a control without renaming it. */}
        {timeZoneUnknown && (
          <Typography variant="body2" color="warning.main" sx={{ fontWeight: 600, mt: "-0.5rem" }} id={timeZoneNoteId}>
            {/* The server validates against ITS tzdata, which can be newer than
                this browser's. A zone it accepts but the browser cannot format
                saves fine and then quietly sends every date field back to the
                device's day — the one thing this whole slice removes. */}
            {t("timezoneUnknownWarning")}
          </Typography>
        )}

        <TextField label={t("localeLabel")} value={locale} required placeholder="en-US"
          onChange={(e) => setLocale(e.target.value)}
          slotProps={{ htmlInput: { maxLength: MAX_LOCALE } }} />

        {/* readOnly, not disabled: a disabled input leaves the tab order, so a
            keyboard user never reaches the field OR the reason it is locked.
            Read-only keeps both, and aria-describedby carries the reason with
            the control. */}
        <TextField label={t("currencyLabel")} value={currencyCode} required
          onChange={(e) => setCurrencyCode(e.target.value.toUpperCase())}
          slotProps={{
            htmlInput: {
              maxLength: 3, readOnly: !loaded.canChangeCurrency,
              "aria-describedby": loaded.canChangeCurrency ? undefined : currencyNoteId,
            },
          }}
          sx={loaded.canChangeCurrency ? undefined : {
            "& .MuiOutlinedInput-root": { backgroundColor: "action.disabledBackground" },
            "& input": { cursor: "not-allowed" },
          }} />
        {/* §4.6: the rule, at the field it locks, rather than as a 422 after
            the user has typed a new code. */}
        {!loaded.canChangeCurrency && (
          <Typography variant="body2" color="warning.main" sx={{ fontWeight: 600, mt: "-0.5rem" }} id={currencyNoteId}>
            {t("currencyLockedNote", { code: loaded.settings.currencyCode })}
          </Typography>
        )}

        <TextField select label={t("unitSystemLabel")} value={unitSystem}
          onChange={(e) => setUnitSystem(e.target.value)}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}>
          {UNIT_SYSTEMS.map((u) => <option key={u} value={u}>{unitSystemLabel(u)}</option>)}
        </TextField>
      </Stack>
          </AccordionDetails>
        </Accordion>

        <Accordion disableGutters slotProps={{ transition: { unmountOnExit: true } }}>
          <AccordionSummary expandIcon={<ChevronDown size={18} aria-hidden />}>
            <Typography variant="h3" component="span">{t("countingSalesSectionHeading")}</Typography>
          </AccordionSummary>
          <AccordionDetails>
      <Stack spacing={2} sx={{ maxWidth: "40rem" }}>

        {/* #444 — the pack unit Daily Entry's steppers bump by, e.g. "+30/-30"
            for Tray, unless a user overrides it for themselves (Header). Codes
            render raw, untranslated, matching ProductsPage's own unitCode cells
            — there is no separate label catalog for this enum anywhere else. */}
        <TextField select label={t("defaultStepperUnitLabel")} value={defaultStepperUnit}
          onChange={(e) => setDefaultStepperUnit(e.target.value)}
          helperText={t("defaultStepperUnitHint")}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}>
          {stepperUnits.filter((u) => u.active).map((u) =>
            <option key={u.unitCode} value={u.unitCode}>{u.unitCode}</option>)}
        </TextField>

        {/* #612 — how a restricted plain Worker's sale confirmation may draw
            stock. Owner/Manager/Sales confirmations stay farm-wide regardless
            of this setting (ReadOnly cannot confirm at all). */}
        <TextField select label={t("workerSaleAllocationPolicyLabel")} value={workerSaleAllocationPolicy}
          onChange={(e) => setWorkerSaleAllocationPolicy(e.target.value)}
          helperText={t("workerSaleAllocationPolicyHint")}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}>
          {WORKER_SALE_ALLOCATION_POLICY_VALUES.map((p) =>
            <option key={p} value={p}>{workerSaleAllocationPolicyLabel(p)}</option>)}
        </TextField>

        {/* #727 — the ceiling a Sales or Worker user's sale lines are held to.
            Whole percents: this is the screen's only numeric input, and
            type="number" disagrees with itself across browsers about `,`
            versus `.` on a screen that formats every other number by locale.
            Storage is basis points, so finer steps cost no migration. */}
        <TextField
          label={t("maxDiscountPercentLabel")}
          type="number"
          value={maxDiscountPercent}
          onChange={(e) => setMaxDiscountPercent(e.target.value)}
          helperText={t("maxDiscountPercentHint")}
          sx={{ maxWidth: "12rem" }}
          slotProps={{ htmlInput: { min: 0, max: 100, step: 1 } }}
        />
      </Stack>
          </AccordionDetails>
        </Accordion>

        <Accordion disableGutters slotProps={{ transition: { unmountOnExit: true } }}>
          <AccordionSummary expandIcon={<ChevronDown size={18} aria-hidden />}>
            <Typography variant="h3" component="span">{t("dateTimeFormatsSectionHeading")}</Typography>
          </AccordionSummary>
          <AccordionDetails>
      <Stack spacing={2} sx={{ maxWidth: "40rem" }}>

        <TextField select label={t("firstDayOfWeekLabel")} value={firstDayOfWeek}
          onChange={(e) => setFirstDayOfWeek(e.target.value)}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}>
          <option value="">{t("followLocaleOption")}</option>
          {WEEKDAYS.map((d) => <option key={d} value={d}>{weekdayLabel(d)}</option>)}
        </TextField>

        <TextField select label={t("dateFormatLabel")}
          value={dateFormatCustom ? CUSTOM_FORMAT_OPTION : dateFormat}
          onChange={(e) => {
            if (e.target.value === CUSTOM_FORMAT_OPTION) { setDateFormatCustom(true); return; }
            setDateFormatCustom(false);
            setDateFormat(e.target.value);
          }}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
        >
          <option value="">{t("followLocaleOption")}</option>
          {DATE_FORMAT_PRESETS.map((p) => <option key={p} value={p}>{p}</option>)}
          <option value={CUSTOM_FORMAT_OPTION}>{t("customFormatOption")}</option>
        </TextField>
        {dateFormatCustom && (
          <TextField label={t("customDateFormatLabel")} value={dateFormat}
            onChange={(e) => setDateFormat(e.target.value)}
            slotProps={{ htmlInput: { maxLength: MAX_FORMAT } }} />
        )}

        <TextField select label={t("timeFormatLabel")}
          value={timeFormatCustom ? CUSTOM_FORMAT_OPTION : timeFormat}
          onChange={(e) => {
            if (e.target.value === CUSTOM_FORMAT_OPTION) { setTimeFormatCustom(true); return; }
            setTimeFormatCustom(false);
            setTimeFormat(e.target.value);
          }}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
        >
          <option value="">{t("followLocaleOption")}</option>
          {TIME_FORMAT_PRESETS.map((p) => <option key={p} value={p}>{p}</option>)}
          <option value={CUSTOM_FORMAT_OPTION}>{t("customFormatOption")}</option>
        </TextField>
        {timeFormatCustom && (
          <TextField label={t("customTimeFormatLabel")} value={timeFormat}
            onChange={(e) => setTimeFormat(e.target.value)}
            slotProps={{ htmlInput: { maxLength: MAX_FORMAT } }} />
        )}
      </Stack>
          </AccordionDetails>
        </Accordion>

        {/* What actually acts on a save today. The timezone reaches every date
            field immediately (#123); the rest are stored on the farm and take
            effect as the screens that would render through them adopt them (#45
            carries the display formatting). Saying "everywhere, straight away"
            would be a promise the app does not keep. */}
        <Typography variant="body2" color="text.secondary">
          {t("effectNote")}
        </Typography>

        <Paper
          data-testid="settings-save-bar"
          square
          sx={{
            position: "fixed", left: { xs: 0, md: "var(--sidebar-w)" }, right: 0,
            bottom: "var(--tabbar-h)", zIndex: "appBar", px: { xs: 2, md: 4 }, py: 1.5,
            borderTop: "1px solid", borderColor: "divider", backgroundColor: "background.paper",
          }}
        >
          <Stack direction="row" sx={{ justifyContent: "space-between", alignItems: "center", flexWrap: "wrap", gap: 1 }}>
            <Typography variant="body2" color="text.secondary" sx={{ m: 0 }}>
              {t("saveScopeNote")}
            </Typography>
            <BusyButton type="submit" busy={saving} disabled={busy || stale}>
              {saving ? t("savingButton") : t("saveButton")}
            </BusyButton>
          </Stack>
          {saveError !== null && <Alert severity="error" sx={{ mt: 1 }}>{saveError}</Alert>}
          <Typography id="settings-status" variant="body2" role="status" color="success.main" sx={{ m: 0 }}>
            {saved && saveError === null ? t("savedMessage") : ""}
          </Typography>
        </Paper>
      </Stack>

      {confirmDialog}
    </section>
  );
}
