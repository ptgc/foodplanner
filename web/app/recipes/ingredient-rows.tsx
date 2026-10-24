import { useEffect, useRef } from "react";
import { useI18n } from "../i18n";
import { ErrorText, RowControls, errorId, fieldProps } from "./form-parts";
import { IngredientPicker } from "./ingredient-picker";
import { emptyIngredient, fieldId, limits, move, type FieldErrors, type IngredientRow, type UnitDimension } from "./recipe-form";
import type { Unit } from "./use-catalog";

type Props = {
  householdId: string;
  rows: IngredientRow[];
  units: Unit[];
  errors: FieldErrors;
  onChange: (rows: IngredientRow[]) => void;
};

const dimensionKeys = {
  Mass: "recipe.unitDimensionMass",
  Volume: "recipe.unitDimensionVolume",
  Count: "recipe.unitDimensionCount",
} as const;

/** Structured ingredient lines: ingredient picker, quantity + unit (or "to taste"), note; add, remove, reorder. */
export function IngredientRows({ householdId, rows, units, errors, onChange }: Props) {
  const { t } = useI18n();
  const addRef = useRef<HTMLButtonElement>(null);
  const pendingFocus = useRef<number | null>(null);

  // After "add", move focus to the new line's first field; after "remove" it goes to the add button (see below).
  useEffect(() => {
    const i = pendingFocus.current;
    if (i === null) return;
    pendingFocus.current = null;
    document.getElementById(fieldId(`ingredients[${i}].ingredientId`))?.focus();
  }, [rows.length]);

  const update = (index: number, patch: Partial<IngredientRow>) =>
    onChange(rows.map((row, i) => (i === index ? { ...row, ...patch } : row)));

  return (
    <div>
      <ErrorText name="ingredients" errors={errors} />
      <ol className="row-list">
        {rows.map((row, index) => {
          const n = index + 1;
          const at = `ingredients[${index}]`;
          return (
            <li key={row.key} className="row-card" data-testid="ingredient-row">
              <fieldset>
                <legend>{t("recipe.ingredientN", { n })}</legend>
                <div className="form-field">
                  <IngredientPicker
                    householdId={householdId}
                    value={row.ingredient}
                    onChange={(ingredient) => update(index, { ingredient })}
                    id={fieldId(`${at}.ingredientId`)}
                    invalid={`${at}.ingredientId` in errors}
                    describedBy={errors[`${at}.ingredientId`] ? errorId(`${at}.ingredientId`) : undefined}
                  />
                  <ErrorText name={`${at}.ingredientId`} errors={errors} />
                </div>

                <label className="check-field">
                  <input
                    type="checkbox"
                    checked={row.toTaste}
                    onChange={(e) =>
                      update(index, e.target.checked ? { toTaste: true, quantity: "", unitId: "" } : { toTaste: false })
                    }
                  />
                  <span>{t("recipe.toTaste")}</span>
                </label>

                {!row.toTaste && (
                  <div className="quantity-unit">
                    <div className="form-field">
                      <label htmlFor={fieldId(`${at}.quantity`)}>{t("recipe.quantity")}</label>
                      <input
                        {...fieldProps(`${at}.quantity`, errors)}
                        inputMode="decimal"
                        autoComplete="off"
                        value={row.quantity}
                        onChange={(e) => update(index, { quantity: e.target.value })}
                      />
                      <ErrorText name={`${at}.quantity`} errors={errors} />
                    </div>
                    <div className="form-field">
                      <label htmlFor={fieldId(`${at}.unitId`)}>{t("recipe.unit")}</label>
                      <UnitSelect
                        {...fieldProps(`${at}.unitId`, errors)}
                        units={units}
                        preferred={row.ingredient?.dimension}
                        value={row.unitId}
                        onChange={(unitId) => update(index, { unitId })}
                      />
                      <ErrorText name={`${at}.unitId`} errors={errors} />
                    </div>
                  </div>
                )}

                <div className="form-field">
                  <label htmlFor={fieldId(`${at}.note`)}>{t("recipe.note")}</label>
                  <input
                    {...fieldProps(`${at}.note`, errors)}
                    value={row.note}
                    onChange={(e) => update(index, { note: e.target.value })}
                  />
                  <ErrorText name={`${at}.note`} errors={errors} />
                </div>

                <RowControls
                  index={index}
                  count={rows.length}
                  upLabel={t("recipe.moveIngredientUp", { n })}
                  downLabel={t("recipe.moveIngredientDown", { n })}
                  removeLabel={t("recipe.removeIngredient", { n })}
                  onMove={(from, to) => onChange(move(rows, from, to))}
                  onRemove={() => {
                    onChange(rows.filter((_, i) => i !== index));
                    addRef.current?.focus();
                  }}
                />
              </fieldset>
            </li>
          );
        })}
      </ol>
      <button
        ref={addRef}
        type="button"
        className="button button-outline"
        disabled={rows.length >= limits.ingredientsMax}
        onClick={() => {
          onChange([...rows, emptyIngredient()]);
          pendingFocus.current = rows.length;
        }}
      >
        {t("recipe.addIngredient")}
      </button>
    </div>
  );
}

type UnitSelectProps = {
  id: string;
  units: Unit[];
  preferred: UnitDimension | undefined;
  value: string;
  onChange: (unitId: string) => void;
  "aria-invalid"?: true;
  "aria-describedby"?: string;
};

/** Units grouped by dimension, the ingredient's own dimension first (all units stay selectable). */
function UnitSelect({ units, preferred, value, onChange, ...rest }: UnitSelectProps) {
  const { t } = useI18n();
  const order = (["Mass", "Volume", "Count"] as const).slice().sort((a, b) => Number(b === preferred) - Number(a === preferred));
  return (
    <select {...rest} value={value} onChange={(e) => onChange(e.target.value)}>
      <option value="">{t("recipe.unitPick")}</option>
      {order.map((dimension) => (
        <optgroup key={dimension} label={t(dimensionKeys[dimension])}>
          {units
            .filter((u) => u.dimension === dimension)
            .map((u) => (
              <option key={u.id} value={u.id}>
                {u.code}
              </option>
            ))}
        </optgroup>
      ))}
    </select>
  );
}
