import { useState } from "react";
import { useI18n } from "../i18n";
import { ErrorText, fieldProps } from "../recipes/form-parts";
import { IngredientPicker } from "../recipes/ingredient-picker";
import { UnitSelect } from "../recipes/ingredient-rows";
import { fieldId, type FieldErrors } from "../recipes/recipe-form";
import type { Unit } from "../recipes/use-catalog";
import type { ImportLine, LineEdit } from "./import-api";
import { buildLineEdit, confidencePercent, initialLineDraft, needsAttention, type LineDraft } from "./import-lines";

type Props = {
  householdId: string;
  index: number;
  line: ImportLine;
  units: Unit[];
  /** Server-side errors for this row, keyed `lines[i].field`. */
  serverErrors: FieldErrors;
  /** True while any request of the draft is running; every action is disabled so edits go one at a time. */
  busy: boolean;
  /** Sends the edit; resolves true when the server accepted it. */
  onApply: (index: number, edit: LineEdit) => Promise<boolean>;
  onRemove: (index: number) => void;
};

const formatOptions = { maximumFractionDigits: 4, useGrouping: false } as const;

/**
 * One imported ingredient line. It always shows the original text (for reference). A line that is not matched is
 * marked with an icon AND words (never colour alone) and opens its editor: pick a suggestion, search the catalog
 * (or create your own ingredient), set amount and unit or "to taste", or remove the line.
 */
export function LineCard({ householdId, index, line, units, serverErrors, busy, onApply, onRemove }: Props) {
  const { t, formatNumber } = useI18n();
  const attention = needsAttention(line);
  const [editing, setEditing] = useState(attention);
  const [draft, setDraft] = useState<LineDraft>(() => initialLineDraft(line, (v) => formatNumber(v, formatOptions)));
  const [localErrors, setLocalErrors] = useState<FieldErrors>({});
  const [applying, setApplying] = useState(false);

  // A new answer from the server (after an edit or a re-read) restarts the editor from what the server now holds.
  const [seen, setSeen] = useState(line);
  if (seen !== line) {
    setSeen(line);
    setDraft(initialLineDraft(line, (v) => formatNumber(v, formatOptions)));
    setEditing(needsAttention(line));
  }

  const showEditor = editing || Object.keys(serverErrors).length > 0;
  const at = `lines[${index}]`;
  const errors: FieldErrors = { ...serverErrors, ...localErrors };
  const set = (patch: Partial<LineDraft>) => {
    setDraft((d) => ({ ...d, ...patch }));
    setLocalErrors({});
  };

  const apply = async (next: LineDraft) => {
    if (busy || applying) return;
    const built = buildLineEdit(index, next);
    if (!built.ok) {
      const prefixed: FieldErrors = {};
      for (const [key, value] of Object.entries(built.errors)) prefixed[`${at}.${key}`] = value;
      setLocalErrors(prefixed);
      const first = Object.keys(prefixed)[0];
      if (first) setTimeout(() => document.getElementById(fieldId(first))?.focus(), 0);
      return;
    }
    setLocalErrors({});
    setApplying(true);
    const ok = await onApply(index, built.edit);
    setApplying(false);
    if (ok) setEditing(false);
  };

  const titleId = `import-line-${index}-title`;
  const unitLabel = line.unitText
    ? t("import.status.unmatchedUnitNamed", { unit: line.unitText })
    : t("import.status.unmatchedUnit");
  const reason =
    line.status === "UnmatchedIngredient"
      ? t("import.status.unmatchedIngredient")
      : line.status === "UnmatchedUnit"
        ? unitLabel
        : t("import.status.unparsed");

  const quantity = line.quantity === null ? null : Number(line.quantity);
  const amount =
    quantity === null ? t("recipe.toTaste") : `${formatNumber(quantity, formatOptions)}${line.unitCode ? ` ${line.unitCode}` : ""}`;

  return (
    <li
      id={`import-line-${index}`}
      tabIndex={-1}
      aria-labelledby={titleId}
      className={`row-card import-line${attention ? " import-line-attention" : ""}`}
      data-testid="import-line"
      data-status={line.status}
    >
      <h3 id={titleId} className="import-line-title">
        {t("import.lineN", { n: index + 1 })}
      </h3>
      <p className="import-original">
        <span className="import-original-label">{t("import.original")}</span> <q>{line.original}</q>
      </p>

      {attention ? (
        <p className="import-status import-status-bad" data-testid="line-status">
          <span aria-hidden="true">&#9888;</span> <span>{reason}</span>
        </p>
      ) : (
        <p className="import-status import-status-ok" data-testid="line-status">
          <span aria-hidden="true">&#10003;</span> <span>{t("import.matched")}</span>
        </p>
      )}

      {!attention && !showEditor && (
        <p className="import-parsed">
          <strong>{line.ingredientName}</strong> &middot; {amount}
          {line.note ? <> &middot; {line.note}</> : null}
          <span className="import-confidence"> &middot; {t("import.confidence", { percent: confidencePercent(line) })}</span>
        </p>
      )}

      {showEditor && (
        <div className="import-editor">
          {line.suggestions.length > 0 && (
            <div role="group" aria-label={t("import.suggestions")} className="import-suggestions">
              <p className="import-editor-heading">{t("import.suggestions")}</p>
              {line.suggestions.map((s) => (
                <button
                  key={s.ingredientId}
                  type="button"
                  className="button button-outline import-suggestion"
                  disabled={busy || applying}
                  onClick={() => void apply({ ...draft, ingredient: { id: s.ingredientId, name: s.name } })}
                >
                  {t("import.useSuggestion", { name: s.name, percent: Math.round(Number(s.confidence) * 100) })}
                </button>
              ))}
            </div>
          )}

          <div className="form-field">
            <p className="import-editor-heading">{t("import.searchHeading")}</p>
            <IngredientPicker
              householdId={householdId}
              value={draft.ingredient}
              onChange={(ingredient) => set({ ingredient })}
              id={fieldId(`${at}.ingredientId`)}
              invalid={`${at}.ingredientId` in errors}
              describedBy={errors[`${at}.ingredientId`] ? `${fieldId(`${at}.ingredientId`)}-error` : undefined}
            />
            <ErrorText name={`${at}.ingredientId`} errors={errors} />
          </div>

          <label className="check-field">
            <input
              type="checkbox"
              checked={draft.toTaste}
              onChange={(e) => set(e.target.checked ? { toTaste: true, quantity: "", unitId: "" } : { toTaste: false })}
            />
            <span>{t("recipe.toTaste")}</span>
          </label>

          {!draft.toTaste && (
            <div className="quantity-unit">
              <div className="form-field">
                <label htmlFor={fieldId(`${at}.quantity`)}>{t("recipe.quantity")}</label>
                <input
                  {...fieldProps(`${at}.quantity`, errors)}
                  inputMode="decimal"
                  autoComplete="off"
                  value={draft.quantity}
                  onChange={(e) => set({ quantity: e.target.value })}
                />
                <ErrorText name={`${at}.quantity`} errors={errors} />
              </div>
              <div className="form-field">
                <label htmlFor={fieldId(`${at}.unitId`)}>{t("recipe.unit")}</label>
                <UnitSelect
                  {...fieldProps(`${at}.unitId`, errors)}
                  units={units}
                  preferred={draft.ingredient?.dimension}
                  value={draft.unitId}
                  onChange={(unitId) => set({ unitId })}
                />
                <ErrorText name={`${at}.unitId`} errors={errors} />
              </div>
            </div>
          )}

          <div className="form-field">
            <label htmlFor={fieldId(`${at}.note`)}>{t("recipe.note")}</label>
            <input {...fieldProps(`${at}.note`, errors)} value={draft.note} onChange={(e) => set({ note: e.target.value })} />
            <ErrorText name={`${at}.note`} errors={errors} />
          </div>

          <div className="form-actions">
            <button type="button" className="button" disabled={busy || applying} onClick={() => void apply(draft)}>
              {applying ? t("import.applying") : t("import.apply")}
            </button>
            {!attention && (
              <button type="button" className="button button-outline" onClick={() => setEditing(false)}>
                {t("import.closeEditor")}
              </button>
            )}
          </div>
        </div>
      )}

      <div className="form-actions import-line-actions">
        {!attention && !showEditor && (
          <button
            type="button"
            className="button button-outline"
            aria-label={t("import.changeLabel", { n: index + 1 })}
            disabled={busy}
            onClick={() => setEditing(true)}
          >
            {t("import.change")}
          </button>
        )}
        <button
          type="button"
          className="button button-outline"
          aria-label={t("import.removeLineLabel", { n: index + 1 })}
          disabled={busy || applying}
          onClick={() => onRemove(index)}
        >
          {t("import.removeLine")}
        </button>
      </div>
    </li>
  );
}
