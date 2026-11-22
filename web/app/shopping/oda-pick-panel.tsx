import { useId, useState } from "react";
import { useI18n, type MessageKey } from "../i18n";
import type { OdaAdded } from "./oda";
import { clearPreference, setPreference, useOnline } from "./oda-preferences";

/**
 * The picker under an added line of the Oda result (T-052): "Choose another product", the inline list of the same fill's
 * alternatives, "Use this next time" and "Remove choice". A lazy chunk with its own message group ("oda"). Picking only calls
 * Foodplanner's own endpoints and never changes the Oda cart. The line owns the state (`has`, `chosen`, `saved`); this panel
 * reports what the server accepted. Everything that came from Oda is rendered as plain text.
 */
export default function OdaPickPanel({
  line,
  householdId,
  has,
  chosen,
  saved,
  onSaved,
  onRemoved,
}: {
  line: OdaAdded;
  householdId: string;
  has: boolean;
  chosen: string | null;
  saved: boolean;
  onSaved: (productId: string) => void;
  onRemoved: () => void;
}) {
  const { t } = useI18n();
  const online = useOnline();
  const listId = useId();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const ingredientId = line.ingredientId;
  if (!ingredientId) return null;

  const pick = async (productId: number) => {
    setBusy(true);
    setError(null);
    const result = await setPreference(householdId, ingredientId, productId);
    setBusy(false);
    if (result.ok) onSaved(String(productId));
    else setError(result.key);
  };

  const remove = async () => {
    setBusy(true);
    setError(null);
    const result = await clearPreference(householdId, ingredientId);
    setBusy(false);
    if (result.ok) onRemoved();
    else setError(result.key);
  };

  return (
    <div className="oda-pick">
      <div className="oda-pick-actions">
        <button
          type="button"
          className="button button-outline oda-pick-button"
          aria-expanded={open}
          aria-controls={listId}
          onClick={() => setOpen((o) => !o)}
        >
          {t("oda.pick.open")}
        </button>
        {has && (
          <button
            type="button"
            className="button button-outline oda-pick-button"
            aria-label={t("oda.pick.removeFor", { name: line.name })}
            disabled={!online || busy}
            onClick={() => void remove()}
          >
            {t("oda.pick.remove")}
          </button>
        )}
      </div>
      {open && (
        <div id={listId} className="oda-pick-panel">
          <ul className="oda-alternatives" aria-label={t("oda.pick.listLabel", { name: line.name })} data-testid="oda-alternatives">
            {line.alternatives.map((a) => {
              const id = String(a.productId);
              const isChosen = chosen === id;
              return (
                <li key={id} className="oda-alternative" data-testid="oda-alternative">
                  <span className="oda-alternative-name">{a.productName}</span>
                  {a.isAdded && <span className="oda-alternative-mark">{t("oda.pick.inCart")}</span>}
                  {isChosen && saved ? (
                    <span className="oda-saved" role="status">
                      {t("oda.pick.saved")}
                    </span>
                  ) : isChosen ? (
                    <span className="oda-alternative-mark">{t("oda.result.choice")}</span>
                  ) : (
                    <button
                      type="button"
                      className="button button-outline oda-pick-button"
                      aria-label={t("oda.pick.useFor", { product: a.productName })}
                      disabled={!online || busy}
                      onClick={() => void pick(Number(a.productId))}
                    >
                      {t("oda.pick.use")}
                    </button>
                  )}
                </li>
              );
            })}
          </ul>
          <p className="shop-hint">{t("oda.pick.note")}</p>
          {!online && <p className="shop-hint">{t("oda.pick.offline")}</p>}
        </div>
      )}
      {error && (
        <p role="alert" className="form-error" data-testid="oda-pick-error">
          {t(error)}
        </p>
      )}
    </div>
  );
}
