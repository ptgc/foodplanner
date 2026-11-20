import { useState } from "react";
import { useI18n } from "../i18n";
import { copyText, fetchExport, shareText } from "./export";

/** Share and Copy for the week on screen. The text is formatted by the API, so both need it to be reachable. */
export function ExportBar({ householdId, weekStart, online }: { householdId: string; weekStart: string; online: boolean }) {
  const { t } = useI18n();
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [manual, setManual] = useState<string | null>(null);

  const run = async (mode: "share" | "copy") => {
    setBusy(true);
    setStatus(null);
    setError(null);
    setManual(null);
    try {
      const payload = await fetchExport(householdId, weekStart);
      if (payload === "failed") {
        setError(t("shopping.exportFailed"));
        return;
      }
      if (mode === "share") {
        const shared = await shareText(payload);
        if (shared !== "unavailable") return; // shared, or the user cancelled: nothing more to do
      }
      if (await copyText(payload.text)) setStatus(t("shopping.copied"));
      else setManual(payload.text);
    } catch {
      setError(t("shopping.exportFailed"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="shop-export">
      <div className="shop-export-buttons">
        <button type="button" className="button" disabled={busy || !online} onClick={() => void run("share")}>
          {t("shopping.share")}
        </button>
        <button type="button" className="button" disabled={busy || !online} onClick={() => void run("copy")}>
          {t("shopping.copy")}
        </button>
      </div>
      {!online && <p className="shop-hint">{t("shopping.exportNeedsOnline")}</p>}
      <p role="status" aria-live="polite" className="shop-toast" data-testid="copy-toast">
        {status}
      </p>
      {error && (
        <p role="alert" className="form-error">
          {error}
        </p>
      )}
      {manual !== null && (
        <div className="field">
          <label htmlFor="shop-export-text">{t("shopping.copyManually")}</label>
          <textarea id="shop-export-text" readOnly rows={8} value={manual} onFocus={(e) => e.currentTarget.select()} />
        </div>
      )}
    </div>
  );
}
