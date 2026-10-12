import { useEffect, useRef, useState } from "react";
import type { ChangeEvent } from "react";
import { Trans, useTranslation } from "react-i18next";
import { Trash2, Upload } from "lucide-react";
import { Alert, Box, Button, Paper, Stack, Typography } from "@mui/material";
import {
  BANNER_ACCEPT, LOGO_ACCEPT, getFarmBanner, removeFarmBanner, removeFarmLogo,
  uploadFarmBanner, uploadFarmLogo,
} from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import type { useConfirm } from "../../components/useConfirm";
import type { usePendingAction } from "../../components/usePendingAction";
import { errText } from "../../lib/errText";
import { farmBindingToken, getBoundFarmCode } from "../../auth/tokenStore";
import { cacheBannerBytes, forgetBannerFor } from "../../lib/bannerCache";
import { useBannerObjectUrl, useLogoObjectUrl } from "../../farm/useLogoObjectUrl";
import i18n from "../../i18n";
import { keyFor } from "./attempt";
import type { Attempt } from "./attempt";

// The page's one shared flight and confirm dialog, passed down so both cards
// join them rather than opening their own (#236).
type PendingFlight = ReturnType<typeof usePendingAction>;
type ConfirmFn = ReturnType<typeof useConfirm>["confirm"];

// The two branding-image uploaders of the identity-images accordion (#123,
// #179): preview, upload and remove, each idempotency-keyed to its exact
// payload. The one shared flight, the confirm dialog and the server caps stay
// with the page and arrive as props; each card owns only its own state.

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

// Keep a real labelled file input so the picker remains keyboard-accessible.
const fileButtonSx = {
  display: "inline-flex", flexDirection: "row", alignItems: "center", gap: "0.4rem",
  cursor: "pointer", fontWeight: 600, fontSize: "0.92rem", color: "primary.contrastText",
  backgroundColor: "primary.main", padding: "0.6rem 1.15rem",
  "&:hover": { backgroundColor: "primary.dark" },
  "&:has(input:disabled)": { opacity: 0.55, cursor: "default" },
  "&:focus-within": { outline: "2px solid", outlineColor: "var(--focus)", outlineOffset: "2px" },
  "& input[type='file']": {
    position: "absolute", width: "1px", height: "1px", opacity: 0, pointerEvents: "none",
  },
} as const;
const previewImgSx = {
  height: 72, maxWidth: 240, objectFit: "contain" as const, backgroundColor: "#fff",
  border: "1px solid var(--hairline)", borderRadius: "var(--r-panel)", padding: "0.5rem",
};

export function LogoCard({
  logoHash, maxUploadBytes, busy, isPending, run, confirm, refresh, applyLogoHash, logoRulesId,
}: {
  logoHash: string | null;
  maxUploadBytes: number;
  busy: boolean;
  isPending: PendingFlight["isPending"];
  run: PendingFlight["run"];
  confirm: ConfirmFn;
  refresh: () => Promise<boolean>;
  applyLogoHash: (contentHash: string | null) => void;
  logoRulesId: string;
}) {
  const { t } = useTranslation("settings");
  const [focusUploadAfterRemove, setFocusUploadAfterRemove] = useState(false);
  const [logoError, setLogoError] = useState<string | null>(null);
  const [logoMessage, setLogoMessage] = useState<string | null>(null);
  const uploadAttempt = useRef<Attempt | null>(null);
  const removeAttempt = useRef<Attempt | null>(null);
  const uploadInput = useRef<HTMLInputElement>(null);

  const hasLogo = logoHash !== null;
  const logo = useLogoObjectUrl(logoHash);
  const maxUploadKb = Math.floor(maxUploadBytes / 1024);
  const logoBusy = isPending("logo:upload") || isPending("logo:remove");

  // Focus lands here only once the upload input is enabled again — a disabled
  // control silently refuses focus() and the keyboard would be left on <body>.
  // Verified rather than assumed, for the same reason.
  useEffect(() => {
    if (!focusUploadAfterRemove || logoBusy) return;
    setFocusUploadAfterRemove(false);
    uploadInput.current?.focus();
  }, [focusUploadAfterRemove, logoBusy]);

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

  return (
              <Paper variant="outlined" data-testid="settings-logo-card" sx={{ flex: 1, borderRadius: 0, p: 2.5 }}>
                <Typography variant="subtitle1" sx={{ fontWeight: 700 }}>{t("logoSectionHeading")}</Typography>
                <Stack direction="row" sx={{ alignItems: "center", gap: "1.25rem", flexWrap: "wrap", my: 1.5 }}>
                  {logo.url !== null ? (
                    <Box component="img" src={logo.url} alt={t("logoAlt")} sx={previewImgSx} />
                  ) : (
                    <Typography variant="body2" color="text.secondary" sx={{ m: 0 }}>
                      {logo.loading ? t("logoLoadingMessage")
                        : logo.failed ? t("logoLoadFailedMessage")
                          : t("logoNoneMessage")}
                    </Typography>
                  )}
                </Stack>
                <Stack direction="row" sx={{ gap: "0.75rem", flexWrap: "wrap", mb: 1.5 }}>
                  <Button component="label" variant="contained" sx={fileButtonSx}>
                    <Upload size={16} aria-hidden /> {hasLogo ? t("replaceLogoButton") : t("uploadLogoButton")}
                    <input ref={uploadInput} type="file" accept={LOGO_ACCEPT} disabled={busy}
                      aria-describedby={logoRulesId}
                      onChange={(e) => void onPickLogo(e)} />
                  </Button>
                  {hasLogo && (
                    <BusyButton type="button" variant="outlined" color="error"
                      sx={{ borderColor: "divider", "&:hover": { borderColor: "divider" } }} disabled={busy}
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
                {/* Keep the upload status region mounted so updates are announced. */}
                <Typography id="logo-status" variant="body2" role="status" color="success.main">
                  {isPending("logo:upload") ? t("logoWorkingMessage") : logoMessage ?? ""}
                </Typography>
                {logoError !== null && <Alert severity="error">{logoError}</Alert>}
              </Paper>
  );
}

export function BannerCard({
  bannerHash, bannerMaxUploadBytes, busy, isPending, run, confirm, refresh, applyBannerHash, bannerRulesId,
}: {
  bannerHash: string | null;
  bannerMaxUploadBytes: number;
  busy: boolean;
  isPending: PendingFlight["isPending"];
  run: PendingFlight["run"];
  confirm: ConfirmFn;
  refresh: () => Promise<boolean>;
  applyBannerHash: (contentHash: string | null) => void;
  bannerRulesId: string;
}) {
  const { t } = useTranslation("settings");
  const [focusBannerUploadAfterRemove, setFocusBannerUploadAfterRemove] = useState(false);
  const [bannerError, setBannerError] = useState<string | null>(null);
  const [bannerMessage, setBannerMessage] = useState<string | null>(null);
  const bannerUploadAttempt = useRef<Attempt | null>(null);
  const bannerRemoveAttempt = useRef<Attempt | null>(null);
  const bannerUploadInput = useRef<HTMLInputElement>(null);
  // Invalidates stale same-farm banner refreshes.
  const bannerOpGeneration = useRef(0);

  const hasBanner = bannerHash !== null;
  const banner = useBannerObjectUrl(bannerHash);
  const bannerMaxUploadKb = Math.floor(bannerMaxUploadBytes / 1024);
  const bannerBusy = isPending("banner:upload") || isPending("banner:remove");

  useEffect(() => {
    if (!focusBannerUploadAfterRemove || bannerBusy) return;
    setFocusBannerUploadAfterRemove(false);
    bannerUploadInput.current?.focus();
  }, [focusBannerUploadAfterRemove, bannerBusy]);

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
        // Cache sanitized server bytes only if this is still the latest operation.
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
    bannerOpGeneration.current += 1;

    await run("banner:remove", async () => {
      try {
        await removeFarmBanner(attempt.key);
        bannerRemoveAttempt.current = null;
        applyBannerHash(null);
        setBannerMessage(i18n.t("settings:bannerRemovedMessage"));
        const slug = getBoundFarmCode();
        if (slug !== null) forgetBannerFor(slug);
        setFocusBannerUploadAfterRemove(true);
        await refresh();
      } catch (err) {
        setBannerError(errText(err));
      }
    });
  }

  return (
              <Paper variant="outlined" data-testid="settings-banner-card" sx={{ flex: 1, borderRadius: 0, p: 2.5 }}>
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
                  <Button component="label" variant="contained" sx={fileButtonSx}>
                    <Upload size={16} aria-hidden /> {hasBanner ? t("replaceBannerButton") : t("uploadBannerButton")}
                    <input ref={bannerUploadInput} type="file" accept={BANNER_ACCEPT} disabled={busy}
                      aria-describedby={bannerRulesId}
                      onChange={(e) => void onPickBanner(e)} />
                  </Button>
                  {hasBanner && (
                    <BusyButton type="button" variant="outlined" color="error"
                      sx={{ borderColor: "divider", "&:hover": { borderColor: "divider" } }} disabled={busy}
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
  );
}
