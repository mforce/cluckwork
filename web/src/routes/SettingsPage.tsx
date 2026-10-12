import { useEffect, useId, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { ChevronDown } from "lucide-react";
import {
  Accordion, AccordionDetails, AccordionSummary, Alert, Paper, Stack, Typography,
} from "@mui/material";
import { getFarmSettings, listEggUnitConversions, updateFarmSettings } from "../api/cluckwork";
import type { EggUnitConversion, FarmSettings, UpdateFarmSettings } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { BusyButton } from "../components/BusyButton";
import { useConfirm } from "../components/useConfirm";
import { usePendingAction } from "../components/usePendingAction";
import { useFarm } from "../farm/useFarm";
import { farmBindingToken } from "../auth/tokenStore";
import { DEFAULT_BRAND, applyBrand, isBrand, previewBrand } from "../lib/brand";
import type { Brand } from "../lib/brand";
import { isKnownTimeZone } from "../lib/dates";
import i18n from "../i18n";
import { BannerCard, LogoCard } from "./settings/IdentityImageCards";
import { CountingSalesSection } from "./settings/CountingSalesSection";
import {
  DATE_FORMAT_PRESETS, DateTimeSection, TIME_FORMAT_PRESETS, isCustomFormat,
} from "./settings/DateTimeSection";
import { LocalizationSection } from "./settings/LocalizationSection";
import { PaletteFieldset } from "./settings/PaletteFieldset";
import { keyFor } from "./settings/attempt";
import type { Attempt } from "./settings/attempt";

export { formatByteCap } from "./settings/IdentityImageCards";

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

// #123/#729 — farm settings (Owner-only). The §4.5 localization set plus the logo, with
// §4.6's currency lock surfaced as a locked field instead of a 422 the user
// only meets after typing.
export function SettingsPage() {
  const { farm, refresh } = useFarm();
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
  const [brand, setBrand] = useState<Brand>(DEFAULT_BRAND);
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

  const [saveError, setSaveError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [staleReason, setStaleReason] = useState<"conflict" | "readback" | null>(null);
  const stale = staleReason !== null;
  const saveAttempt = useRef<Attempt | null>(null);


  const currencyNoteId = useId();
  const timeZoneNoteId = useId();
  const logoRulesId = useId();
  const bannerRulesId = useId();

  const logoHash = loaded?.settings.logoContentHash ?? null;

  const bannerHash = loaded?.settings.bannerContentHash ?? null;

  // Server config, carried on the settings payload — never a client constant,
  // so it cannot drift from what the server enforces (#123, #179).
  const maxUploadBytes = loaded?.logoMaxUploadBytes ?? 0;
  const bannerMaxUploadBytes = loaded?.bannerMaxUploadBytes ?? 0;

  const timeZoneUnknown = timeZoneId.trim() !== "" && !isKnownTimeZone(timeZoneId.trim());
  const savedBrand = loaded === null ? null : isBrand(loaded.settings.brand) ? loaded.settings.brand : DEFAULT_BRAND;
  // A successful settings read-back can be newer than /account when its refresh fails.
  const baselineBrand = farm !== null && loaded !== null && farm.version > loaded.settings.version
    ? (isBrand(farm.brand) ? farm.brand : DEFAULT_BRAND)
    : savedBrand;
  const displayedBrand = baselineBrand !== null && (staleReason === "conflict" || brand === savedBrand)
    ? baselineBrand : brand;

  useEffect(() => {
    if (baselineBrand === null) return;
    const boundAt = farmBindingToken();
    previewBrand(displayedBrand, boundAt);
    return () => previewBrand(baselineBrand, boundAt);
  }, [displayedBrand, baselineBrand, farm]);

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

  async function onSave(e: FormEvent) {
    e.preventDefault();
    // In-flight re-entry (the old `saving || logoBusy` check) is the hook's
    // job now: run() below skips while any flight is open.
    if (stale || loaded === null || savedBrand === null) return;
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
      // The write may finish after a farm switch; only this binding may receive its palette.
      const boundAt = farmBindingToken();

      try {
        await updateFarmSettings(body, attempt.key);
      } catch (err) {
        setBrand(savedBrand);
        if (err instanceof ApiError && err.status === 409) {
          // The version this screen holds is now definitively wrong, and a retry
          // sends the same one: the middleware caches only 2xx, so it re-executes
          // and 409s again, forever. Disable the button so it agrees with the
          // message rather than inviting the loop (pi round 2).
          setStaleReason("conflict");
          setSaveError(i18n.t("settings:versionConflictMessage"));
          await refresh();
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
        const fresh = await load();
        // Applied from THIS response rather than waiting on refresh() below:
        // refresh() cannot throw (the provider has to survive a failed read), so
        // a successful save with a failed refresh would otherwise leave the old
        // palette live and cached while the authoritative value was in hand (#149).
        applyBrand(fresh.settings.brand, boundAt);
      } catch {
        setLoaded({
          ...loaded,
          settings: { ...loaded.settings, brand },
        });
        applyBrand(brand, boundAt);
        setStaleReason("readback");
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

  return (
    <section>
      <Typography variant="overline" color="text.secondary" component="p" sx={{ m: 0 }}>
        {t("eyebrow")}
      </Typography>
      <Typography variant="h2">{t("heading")}</Typography>
      <Typography variant="body2" color="text.secondary">
        {t("intro")}
      </Typography>

      <Stack component="form" spacing={2} sx={{ mt: 3, pb: "6rem", maxWidth: "760px" }} onSubmit={(e) => void onSave(e)}>
        <Accordion defaultExpanded disableGutters>
          <AccordionSummary expandIcon={<ChevronDown size={18} aria-hidden />}>
            <Typography variant="h3" component="span">{t("identityImagesHeading")}</Typography>
          </AccordionSummary>
          <AccordionDetails>
            <Stack direction={{ xs: "column", md: "row" }} spacing={2.5}>
              <LogoCard
                logoHash={logoHash} maxUploadBytes={maxUploadBytes} busy={busy} isPending={isPending}
                run={run} confirm={confirm} refresh={refresh} applyLogoHash={applyLogoHash}
                logoRulesId={logoRulesId}
              />

              <BannerCard
                bannerHash={bannerHash} bannerMaxUploadBytes={bannerMaxUploadBytes} busy={busy}
                isPending={isPending} run={run} confirm={confirm} refresh={refresh}
                applyBannerHash={applyBannerHash} bannerRulesId={bannerRulesId}
              />
            </Stack>

            <PaletteFieldset
              brand={brand} displayedBrand={displayedBrand} setBrand={setBrand}
              saving={saving} stale={stale} savedBrand={savedBrand}
            />
          </AccordionDetails>
        </Accordion>

        <Accordion disableGutters slotProps={{ transition: { unmountOnExit: true } }}>
          <AccordionSummary expandIcon={<ChevronDown size={18} aria-hidden />}>
            <Typography variant="h3" component="span">{t("localizationSectionHeading")}</Typography>
          </AccordionSummary>
          <AccordionDetails>
          <LocalizationSection
            name={name} setName={setName} timeZoneId={timeZoneId} setTimeZoneId={setTimeZoneId}
            locale={locale} setLocale={setLocale} currencyCode={currencyCode} setCurrencyCode={setCurrencyCode}
            unitSystem={unitSystem} setUnitSystem={setUnitSystem} timeZoneUnknown={timeZoneUnknown}
            timeZoneNoteId={timeZoneNoteId} currencyNoteId={currencyNoteId} loaded={loaded}
          />
          </AccordionDetails>
        </Accordion>

        <Accordion disableGutters slotProps={{ transition: { unmountOnExit: true } }}>
          <AccordionSummary expandIcon={<ChevronDown size={18} aria-hidden />}>
            <Typography variant="h3" component="span">{t("countingSalesSectionHeading")}</Typography>
          </AccordionSummary>
          <AccordionDetails>
      <CountingSalesSection
        defaultStepperUnit={defaultStepperUnit} setDefaultStepperUnit={setDefaultStepperUnit}
        stepperUnits={stepperUnits} workerSaleAllocationPolicy={workerSaleAllocationPolicy}
        setWorkerSaleAllocationPolicy={setWorkerSaleAllocationPolicy}
        maxDiscountPercent={maxDiscountPercent} setMaxDiscountPercent={setMaxDiscountPercent}
      />
          </AccordionDetails>
        </Accordion>

        <Accordion disableGutters slotProps={{ transition: { unmountOnExit: true } }}>
          <AccordionSummary expandIcon={<ChevronDown size={18} aria-hidden />}>
            <Typography variant="h3" component="span">{t("dateTimeFormatsSectionHeading")}</Typography>
          </AccordionSummary>
          <AccordionDetails>
      <DateTimeSection
        firstDayOfWeek={firstDayOfWeek} setFirstDayOfWeek={setFirstDayOfWeek}
        dateFormat={dateFormat} setDateFormat={setDateFormat}
        dateFormatCustom={dateFormatCustom} setDateFormatCustom={setDateFormatCustom}
        timeFormat={timeFormat} setTimeFormat={setTimeFormat}
        timeFormatCustom={timeFormatCustom} setTimeFormatCustom={setTimeFormatCustom}
      />
          </AccordionDetails>
        </Accordion>

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
            <BusyButton variant="contained" type="submit" busy={saving} disabled={busy || stale}>
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
