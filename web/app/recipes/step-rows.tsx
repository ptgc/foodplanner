import { useEffect, useRef } from "react";
import { useI18n } from "../i18n";
import { ErrorText, RowControls, fieldProps } from "./form-parts";
import { emptyStep, fieldId, limits, move, type FieldErrors, type StepRow } from "./recipe-form";

type Props = {
  rows: StepRow[];
  errors: FieldErrors;
  onChange: (rows: StepRow[]) => void;
};

/** Ordered steps: one textarea each, with move up/down and remove (44px). The order is the cooking order. */
export function StepRows({ rows, errors, onChange }: Props) {
  const { t } = useI18n();
  const addRef = useRef<HTMLButtonElement>(null);
  const pendingFocus = useRef<number | null>(null);

  useEffect(() => {
    const i = pendingFocus.current;
    if (i === null) return;
    pendingFocus.current = null;
    document.getElementById(fieldId(`steps[${i}].text`))?.focus();
  }, [rows.length]);

  return (
    <div>
      <ErrorText name="steps" errors={errors} />
      <ol className="row-list">
        {rows.map((row, index) => {
          const n = index + 1;
          const key = `steps[${index}].text`;
          return (
            <li key={row.key} className="row-card" data-testid="step-row">
              <div className="form-field">
                <label htmlFor={fieldId(key)}>{t("recipe.stepN", { n })}</label>
                <textarea
                  {...fieldProps(key, errors)}
                  rows={3}
                  value={row.text}
                  onChange={(e) => onChange(rows.map((r, i) => (i === index ? { ...r, text: e.target.value } : r)))}
                />
                <ErrorText name={key} errors={errors} />
              </div>
              <RowControls
                index={index}
                count={rows.length}
                upLabel={t("recipe.moveStepUp", { n })}
                downLabel={t("recipe.moveStepDown", { n })}
                removeLabel={t("recipe.removeStep", { n })}
                onMove={(from, to) => onChange(move(rows, from, to))}
                onRemove={() => {
                  onChange(rows.filter((_, i) => i !== index));
                  addRef.current?.focus();
                }}
              />
            </li>
          );
        })}
      </ol>
      <button
        ref={addRef}
        type="button"
        className="button button-outline"
        disabled={rows.length >= limits.stepsMax}
        onClick={() => {
          onChange([...rows, emptyStep()]);
          pendingFocus.current = rows.length;
        }}
      >
        {t("recipe.addStep")}
      </button>
    </div>
  );
}
