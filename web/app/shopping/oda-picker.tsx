import { useId, useState } from "react";
import { useI18n, type MessageKey } from "../i18n";
import { type OdaAdded, quantityText } from "./oda";
import { clearPreference, setPreference, useOnline } from "./oda-preferences";

type Change = "set" | "removed" | null;

/**
 * One added line of the Oda result (T-052): the text, its labels ("Your Oda choice", "Usually bought", why the choice was not
 * used) and, for an Adult on a finished fill, the picker. Picking only calls Foodplanner's own endpoints; it never changes the
 * Oda cart. Names and ids of the products live in this response and in React state only, never in browser storage. Everything
 * that came from Oda is rendered as plain text.
 */
export function OdaAddedLine({ line, householdId, interactive }: { line: OdaAdded; householdId: string; interactive: boolean }) {
  const { t, formatNumber } = useI18n();
  const online = useOnline();
  const listId = useId();
  const initial = line.alternatives.find((a) => a.isPreferred);
  const [has, setHas] = useState(line.hasPreference);
  const [chosen, setChosen] = useState<string | null>(initial ? String(initial.productId) : null);
  const [change, setChange] = useState<Change>(null);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);

  const quantity = quantityText(line.quantity, formatNumber);
  const text =
    quantity === null
      ? t("oda.result.addedLineNoQuantity", { name: line.name, productName: line.productName })
      : t("oda.result.addedLine", { name: line.name, productName: line.productName, quantity });

  // What the line says about the choice: the household's pick (original or just saved), or why the pick was not used.
  const showChoice = change === "set" || (change === null && line.source === "preferred");
  const showUsual = line.source === "usually-bought" && change !== "set";
  const problem = change === null ? line.preferenceProblem : null;
  const ingredientId = line.ingredientId;
  const canPick = interactive && ingredientId !== null && (line.alternatives.length >= 2 || has);

  const pick = async (productId: number) => {
    if (!ingredientId) return;
    setBusy(true);
    setError(null);
    const result = await setPreference(householdId, ingredientId, productId);
    setBusy(false);
    if (result.ok) {
      setHas(true);
      setChosen(String(productId));
      setChange("set");
    } else setError(result.key);
  };

  const remove = async () => {
    if (!ingredientId) return;
    setBusy(true);
    setError(null);
    const result = await clearPreference(householdId, ingredientId);
    setBusy(false);
    if (result.ok) {
      setHas(false);
      setChosen(null);
      setChange("removed");
    } else setError(result.key);
  };

  return (
    <>
      <span className="oda-line-text">{text}</span>
      {showUsual && (
        <span className="oda-usual" data-testid="oda-usual">
          {t("oda.result.usual")}
        </span>
      )}
      {showChoice && (
        <span className="oda-usual oda-choice" data-testid="oda-choice">
          {t("oda.result.choice")}
        </span>
      )}
      {problem && (
        <span className="oda-problem" data-testid="oda-choice-problem">
          {t(problem === "unavailable" ? "oda.result.problem.unavailable" : "oda.result.problem.not-found")}
        </span>
      )}
      {change === "removed" && (
        <span className="oda-saved" role="status" data-testid="oda-removed">
          {t("oda.pick.removed")}
        </span>
      )}
      {canPick && (
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
                      {isChosen && change === "set" ? (
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
      )}
    </>
  );
}
