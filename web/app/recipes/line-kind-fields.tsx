import { useState } from "react";
import { useI18n } from "../i18n";
import { ErrorText } from "./form-parts";
import { IngredientPicker } from "./ingredient-picker";
import { amountKinds, purposes, type AmountKind, type Purpose } from "./prep-list";
import { fieldId, limits, type FieldErrors, type IngredientChoice } from "./recipe-form";

/** What an ingredient line says besides its ingredient and amount (T-076). Shared by the recipe form and the import review. */
export type LineKindValue = {
  amountKind: AmountKind;
  purpose: Purpose;
  isOptional: boolean;
  alternatives: IngredientChoice[];
};

/** "Measured amount / To taste / As needed". Choosing a kind that has no amount clears quantity and unit in the caller. */
export function AmountKindSelect({
  id,
  value,
  onChange,
}: {
  id: string;
  value: AmountKind;
  onChange: (kind: AmountKind) => void;
}) {
  const { t } = useI18n();
  return (
    <div className="form-field line-kind">
      <label htmlFor={id}>{t("recipe.amountKind")}</label>
      <select id={id} value={value} onChange={(e) => onChange(e.target.value as AmountKind)}>
        {amountKinds.map((kind) => (
          <option key={kind} value={kind}>
            {t(`recipe.amountKind.${kind}`)}
          </option>
        ))}
      </select>
    </div>
  );
}

type MetaProps = {
  householdId: string;
  /** The row's error key prefix, e.g. `ingredients[2]` or `lines[2]`. */
  at: string;
  /** The line's own ingredient: it cannot be its own alternative. */
  ingredientId: string | undefined;
  value: LineKindValue;
  errors: FieldErrors;
  onChange: (patch: Partial<LineKindValue>) => void;
};

/** Purpose, optional and alternatives. Alternatives are picked with the same ingredient picker as the line itself. */
export function LineMetaFields({ householdId, at, ingredientId, value, errors, onChange }: MetaProps) {
  const { t } = useI18n();
  const [adding, setAdding] = useState(false);
  const pickerId = fieldId(`${at}.alternativeIds`);
  const taken = (id: string) => id === ingredientId || value.alternatives.some((a) => a.id === id);

  return (
    <>
      <div className="form-field line-kind">
        <label htmlFor={fieldId(`${at}.purpose`)}>{t("recipe.purpose")}</label>
        <select
          id={fieldId(`${at}.purpose`)}
          value={value.purpose}
          onChange={(e) => onChange({ purpose: e.target.value as Purpose })}
        >
          {purposes.map((purpose) => (
            <option key={purpose} value={purpose}>
              {t(`recipe.purpose.${purpose}`)}
            </option>
          ))}
        </select>
      </div>

      <label className="check-field">
        <input type="checkbox" checked={value.isOptional} onChange={(e) => onChange({ isOptional: e.target.checked })} />
        <span>{t("recipe.optional")}</span>
      </label>

      <fieldset className="alternatives" data-testid="alternatives">
        <legend>{t("recipe.alternatives")}</legend>
        <p className="alternatives-hint">{t("recipe.alternativesHint")}</p>
        <ErrorText name={`${at}.alternativeIds`} errors={errors} />
        {value.alternatives.length > 0 && (
          <ul className="alternative-list">
            {value.alternatives.map((alternative, j) => (
              <li key={alternative.id} className="alternative-item">
                <span className="alternative-name">{alternative.name}</span>
                <button
                  type="button"
                  className="button button-outline"
                  aria-label={t("recipe.alternativeRemove", { name: alternative.name })}
                  onClick={() => onChange({ alternatives: value.alternatives.filter((_, k) => k !== j) })}
                >
                  &times;
                </button>
                <ErrorText name={`${at}.alternativeIds[${j}]`} errors={errors} />
              </li>
            ))}
          </ul>
        )}
        {adding ? (
          <div className="form-field">
            <IngredientPicker
              householdId={householdId}
              value={null}
              id={pickerId}
              onChange={(choice) => {
                if (choice && !taken(choice.id) && value.alternatives.length < limits.alternativesMax) {
                  onChange({ alternatives: [...value.alternatives, choice] });
                }
                setAdding(false);
              }}
            />
            <button type="button" className="button button-outline" onClick={() => setAdding(false)}>
              {t("recipe.cancel")}
            </button>
          </div>
        ) : (
          <button
            type="button"
            className="button button-outline"
            disabled={value.alternatives.length >= limits.alternativesMax}
            onClick={() => setAdding(true)}
          >
            {t("recipe.alternativeAdd")}
          </button>
        )}
      </fieldset>
    </>
  );
}
