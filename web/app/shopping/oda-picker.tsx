import { Suspense, useState } from "react";
import { useI18n } from "../i18n";
import { lazyWithMessages } from "../i18n/lazy";
import { type OdaAdded, quantityText } from "./oda";

// The picker (list of products, buttons, its texts) is its own chunk with its own message group: only an Adult who looks at a
// finished fill with something to choose from ever loads it.
const OdaPickPanel = lazyWithMessages("oda", () => import("./oda-pick-panel"));

type Change = "set" | "removed" | null;

/**
 * One added line of the Oda result (T-052): the text and its labels ("Your Oda choice", "Usually bought", why the choice was
 * not used), and, for an Adult on a finished fill, the picker. Names and ids of the products live in this response and in React
 * state only, never in browser storage. Everything that came from Oda is rendered as plain text.
 */
export function OdaAddedLine({ line, householdId, interactive }: { line: OdaAdded; householdId: string; interactive: boolean }) {
  const { t, formatNumber } = useI18n();
  const initial = line.alternatives.find((a) => a.isPreferred);
  const [has, setHas] = useState(line.hasPreference);
  const [chosen, setChosen] = useState<string | null>(initial ? String(initial.productId) : null);
  const [change, setChange] = useState<Change>(null);

  const quantity = quantityText(line.quantity, formatNumber);
  const text =
    quantity === null
      ? t("oda.result.addedLineNoQuantity", { name: line.name, productName: line.productName })
      : t("oda.result.addedLine", { name: line.name, productName: line.productName, quantity });

  // What the line says about the choice: the household's pick (original or just saved), or why the pick was not used.
  const showChoice = change === "set" || (change === null && line.source === "preferred");
  const showUsual = line.source === "usually-bought" && change !== "set";
  const problem = change === null ? line.preferenceProblem : null;
  const canPick = interactive && line.ingredientId !== null && (line.alternatives.length >= 2 || has);

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
        <Suspense fallback={null}>
          <OdaPickPanel
            line={line}
            householdId={householdId}
            has={has}
            chosen={chosen}
            saved={change === "set"}
            onSaved={(id) => {
              setHas(true);
              setChosen(id);
              setChange("set");
            }}
            onRemoved={() => {
              setHas(false);
              setChosen(null);
              setChange("removed");
            }}
          />
        </Suspense>
      )}
    </>
  );
}
