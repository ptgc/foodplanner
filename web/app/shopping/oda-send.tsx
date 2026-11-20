import { useState } from "react";
import { ConfirmDialog } from "../household/confirm-dialog";
import { useI18n, type MessageKey } from "../i18n";
import { navigation, rememberPending, startOda, type OdaStartResult } from "./oda";

type Refusal = Extract<OdaStartResult, { kind: "refused" }>["reason"];

const refusalKeys: Record<Refusal, MessageKey> = {
  "not-configured": "oda.problem.notConfigured",
  "nothing-to-add": "oda.problem.nothingToAdd",
  "too-many": "oda.problem.tooMany",
  "rate-limited": "problem.rateLimited",
  forbidden: "problem.forbidden",
  failed: "problem.generic",
};

type Props = {
  householdId: string;
  weekStart: string;
  /** Names of the unchecked "To buy" items: exactly what the server will send. Shown as plain text. */
  names: string[];
  online: boolean;
};

/**
 * "Add to Oda cart" (T-054). Adults only: the caller does not render it for anyone else (kid mode has its own shell).
 * The confirmation lists what will be sent; confirming asks the API for Oda's sign-in address and leaves the page.
 */
export function OdaSend({ householdId, weekStart, names, online }: Props) {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  // The server said Oda is not set up (404): the button goes away and only the text export remains.
  const [unavailable, setUnavailable] = useState(false);

  if (unavailable) {
    return (
      <p className="shop-hint" data-testid="oda-unavailable">
        {t("oda.notConfigured")}
      </p>
    );
  }

  const confirm = async () => {
    setBusy(true);
    setError(null);
    const result = await startOda(householdId, weekStart);
    if (result.kind === "redirect") {
      rememberPending({ householdId, weekStart });
      // A full-page navigation to Oda. The dialog stays busy: the page is going away.
      navigation.go(result.url);
      return;
    }
    setBusy(false);
    if (result.reason === "not-configured") {
      setOpen(false);
      setUnavailable(true);
      return;
    }
    setError(refusalKeys[result.reason]);
  };

  const nothing = names.length === 0;
  return (
    <div className="oda-send">
      <button type="button" className="button button-outline oda-button" disabled={!online || nothing} onClick={() => setOpen(true)}>
        {t("oda.button")}
      </button>
      {!online && <p className="shop-hint">{t("oda.needsOnline")}</p>}
      {online && nothing && <p className="shop-hint">{t("oda.nothingToSend")}</p>}
      {open && (
        <ConfirmDialog
          title={t("oda.confirm.title")}
          body={t("oda.confirm.items", { count: names.length })}
          confirmLabel={t("oda.confirm.submit")}
          tone="primary"
          busy={busy}
          error={error}
          onConfirm={() => void confirm()}
          onCancel={() => setOpen(false)}
        >
          <ul className="oda-items" aria-label={t("oda.confirm.itemsLabel")} data-testid="oda-items">
            {names.map((name, i) => (
              <li key={i}>{name}</li>
            ))}
          </ul>
          <ul className="oda-notes">
            <li>{t("oda.confirm.note.signIn")}</li>
            <li>{t("oda.confirm.note.onePack")}</li>
            <li>{t("oda.confirm.note.noOrder")}</li>
            <li>{t("oda.confirm.note.twice")}</li>
          </ul>
        </ConfirmDialog>
      )}
    </div>
  );
}
