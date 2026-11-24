import { useEffect, useState } from "react";
import { ConfirmDialog } from "../household/confirm-dialog";
import { useI18n, type MessageKey } from "../i18n";
import { navigation, rememberSyncPending, safeHttpUrl } from "./oda";
import { startSync, type OdaSyncOptions, type OdaSyncStartResult } from "./oda-sync";

type Preview =
  | { status: "loading" }
  | { status: "ready"; url: string; count: number; left: number }
  | { status: "refused"; key: MessageKey };

const refusalKeys: Record<Extract<OdaSyncStartResult, { kind: "refused" }>["reason"], MessageKey> = {
  "not-configured": "oda.sync.notConfigured",
  "nothing-to-sync": "oda.sync.problem.nothing",
  "rate-limited": "problem.rateLimited",
  forbidden: "problem.forbidden",
  failed: "problem.generic",
};

/**
 * The "Sync with Oda" button and its confirmation (T-130). Adults only: the caller does not render it for anyone else. Opening the
 * confirmation asks the API how many ingredients the run will search (no call to Oda is made); the two boxes are off and change that
 * count. Confirming remembers the household and the options (flags and numbers only) and leaves for Oda's sign-in.
 */
export function OdaSyncStart({ householdId, online }: { householdId: string; online: boolean }) {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  const [staples, setStaples] = useState(false);
  const [existing, setExisting] = useState(false);
  // The answer of the latest count, tagged with the options it was asked for: an answer for other options counts as "loading".
  const [answer, setAnswer] = useState<{ key: string; value: Exclude<Preview, { status: "loading" }> } | null>(null);
  // The server says Oda is not set up (404): the button goes away.
  const [unavailable, setUnavailable] = useState(false);
  const key = `${staples}|${existing}`;
  const preview: Preview = answer?.key === key ? answer.value : { status: "loading" };

  useEffect(() => {
    if (!open) return;
    let live = true; // a newer count (another box ticked) is on its way when this is false
    void (async () => {
      const result = await startSync(householdId, { includeStaples: staples, includeExisting: existing, offset: 0 });
      if (!live) return;
      if (result.kind === "ready") {
        setAnswer({ key, value: { status: "ready", url: result.url, count: result.ingredientCount, left: result.notCheckedCount } });
      } else if (result.reason === "not-configured") {
        setOpen(false);
        setUnavailable(true);
      } else setAnswer({ key, value: { status: "refused", key: refusalKeys[result.reason] } });
    })();
    return () => {
      live = false;
    };
  }, [open, key, staples, existing, householdId]);

  if (unavailable) {
    return (
      <p className="shop-hint" data-testid="oda-sync-unavailable">
        {t("oda.sync.notConfigured")}
      </p>
    );
  }

  const confirm = () => {
    if (preview.status !== "ready") return;
    const url = safeHttpUrl(preview.url);
    if (!url) return;
    const options: OdaSyncOptions = { includeStaples: staples, includeExisting: existing, offset: 0 };
    rememberSyncPending({ householdId, ...options });
    // A full-page navigation to Oda. The dialog stays as it is: the page is going away.
    navigation.go(url);
  };

  return (
    <div className="oda-sync-start" data-testid="oda-sync-start">
      <p>{t("oda.sync.intro")}</p>
      <button type="button" className="button oda-button" disabled={!online} onClick={() => setOpen(true)}>
        {t("oda.sync.button")}
      </button>
      {!online && (
        <p className="shop-hint" data-testid="oda-sync-offline">
          {t("oda.sync.offline")}
        </p>
      )}
      {open && (
        <ConfirmDialog
          title={t("oda.sync.confirm.title")}
          body={
            preview.status === "ready"
              ? t("oda.sync.confirm.count", { count: preview.count })
              : preview.status === "loading"
                ? t("oda.sync.confirm.counting")
                : t(preview.key)
          }
          confirmLabel={t("oda.sync.confirm.submit")}
          tone="primary"
          busy={false}
          confirmDisabled={preview.status !== "ready"}
          error={null}
          onConfirm={confirm}
          onCancel={() => setOpen(false)}
        >
          {preview.status === "ready" && preview.left > 0 && (
            <p data-testid="oda-sync-left">{t("oda.sync.confirm.left", { count: preview.left })}</p>
          )}
          <label className="oda-sync-check">
            <input type="checkbox" checked={staples} onChange={(e) => setStaples(e.target.checked)} />
            <span>{t("oda.sync.confirm.staples")}</span>
          </label>
          <label className="oda-sync-check">
            <input type="checkbox" checked={existing} onChange={(e) => setExisting(e.target.checked)} />
            <span>{t("oda.sync.confirm.existing")}</span>
          </label>
          <ul className="oda-notes">
            <li>{t("oda.sync.confirm.note.signIn")}</li>
            <li>{t("oda.sync.confirm.note.noCart")}</li>
            <li>{t("oda.sync.confirm.note.kept")}</li>
          </ul>
        </ConfirmDialog>
      )}
    </div>
  );
}
