import { useEffect, useId, useState } from "react";
import { useI18n } from "../i18n";
import { problemKey } from "../i18n/problems";
import { failureText } from "../routes/recipes";
import type { IngredientChoice } from "./recipe-form";
import { createIngredient, searchIngredients, type Ingredient } from "./use-catalog";
import type { Failure } from "./use-recipes";

export const searchDelayMs = 250;

type Props = {
  householdId: string;
  value: IngredientChoice | null;
  onChange: (choice: IngredientChoice | null) => void;
  /** Id for the interactive element, so the error summary can focus it. */
  id: string;
  describedBy?: string;
  invalid?: boolean;
};

const toChoice = (i: Ingredient): IngredientChoice => ({ id: i.id, name: i.name, dimension: i.dimension });

/**
 * Picks an ingredient from the catalog (global and the household's own) by searching `?search=`, or creates the
 * household's own ingredient inline. Rule 5: a recipe line always points at an ingredient id, never at free text.
 */
export function IngredientPicker({ householdId, value, onChange, id, describedBy, invalid }: Props) {
  const { t } = useI18n();
  const [term, setTerm] = useState("");
  const [found, setFound] = useState<{ term: string; items: Ingredient[] } | null>(null);
  const [failure, setFailure] = useState<Failure | null>(null);
  const [creating, setCreating] = useState(false);

  const trimmed = term.trim();
  useEffect(() => {
    if (value || trimmed === "") return;
    let cancelled = false;
    const timer = setTimeout(() => {
      void searchIngredients(householdId, trimmed).then((result) => {
        if (cancelled) return;
        if (result.ok) {
          setFound({ term: trimmed, items: result.value });
          setFailure(null);
        } else setFailure(result.failure);
      });
    }, searchDelayMs);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [householdId, trimmed, value]);

  if (value) {
    return (
      <div className="ingredient-chosen">
        <span className="ingredient-chosen-name">{t("recipe.ingredientChosen", { name: value.name })}</span>
        <button
          type="button"
          id={id}
          className="button button-outline"
          aria-describedby={describedBy}
          onClick={() => {
            setTerm("");
            setFound(null);
            onChange(null);
          }}
        >
          {t("recipe.ingredientChange")}
        </button>
      </div>
    );
  }

  const current = found && found.term === trimmed ? found.items : null;
  const exact = current?.some((i) => i.name.toLocaleLowerCase() === trimmed.toLocaleLowerCase()) ?? false;

  return (
    <div className="ingredient-picker">
      <label htmlFor={id}>{t("recipe.ingredientSearch")}</label>
      <input
        id={id}
        type="search"
        value={term}
        autoComplete="off"
        enterKeyHint="search"
        placeholder={t("recipe.ingredientSearchHint")}
        aria-describedby={describedBy}
        aria-invalid={invalid || undefined}
        onChange={(e) => {
          setTerm(e.target.value);
          setCreating(false);
        }}
      />
      {failure && <p role="alert">{failureText(t, failure)}</p>}
      {current && current.length > 0 && (
        <ul className="ingredient-results" aria-label={t("recipe.ingredientResults")}>
          {current.map((item) => (
            <li key={item.id}>
              <button type="button" className="ingredient-result" onClick={() => onChange(toChoice(item))}>
                <span>{item.name}</span>
                {!item.isGlobal && <span className="tag-pill">{t("recipe.ingredientOwn")}</span>}
              </button>
            </li>
          ))}
        </ul>
      )}
      {current && current.length === 0 && <p className="ingredient-none">{t("recipe.ingredientNone")}</p>}
      {current && trimmed !== "" && !exact && !creating && (
        <button type="button" className="button button-outline ingredient-create" onClick={() => setCreating(true)}>
          {t("recipe.ingredientCreate", { name: trimmed })}
        </button>
      )}
      {creating && (
        <CreateIngredient
          householdId={householdId}
          initialName={trimmed}
          onCancel={() => setCreating(false)}
          onCreated={(ingredient) => {
            setCreating(false);
            onChange(toChoice(ingredient));
          }}
        />
      )}
    </div>
  );
}

function CreateIngredient({
  householdId,
  initialName,
  onCancel,
  onCreated,
}: {
  householdId: string;
  initialName: string;
  onCancel: () => void;
  onCreated: (ingredient: Ingredient) => void;
}) {
  const { t } = useI18n();
  const nameId = useId();
  const dimensionId = useId();
  const [name, setName] = useState(initialName);
  const [dimension, setDimension] = useState<Ingredient["dimension"]>("Mass");
  const [staple, setStaple] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    if (busy || name.trim() === "") return;
    setBusy(true);
    setError(null);
    const result = await createIngredient(householdId, { name: name.trim(), dimension, isStaple: staple });
    setBusy(false);
    if (result.ok) onCreated(result.value);
    else setError(result.failure === "unreachable" ? t("problem.unreachable") : t(problemKey(result.failure.problemType)));
  };

  return (
    <fieldset className="ingredient-create-form">
      <legend>{t("recipe.ingredientCreateTitle")}</legend>
      <div className="form-field">
        <label htmlFor={nameId}>{t("recipe.ingredientName")}</label>
        <input id={nameId} value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
      </div>
      <div className="form-field">
        <label htmlFor={dimensionId}>{t("recipe.ingredientDimension")}</label>
        <select id={dimensionId} value={dimension} onChange={(e) => setDimension(e.target.value as Ingredient["dimension"])}>
          <option value="Mass">{t("recipe.dimensionMass")}</option>
          <option value="Volume">{t("recipe.dimensionVolume")}</option>
          <option value="Count">{t("recipe.dimensionCount")}</option>
        </select>
      </div>
      <label className="check-field">
        <input type="checkbox" checked={staple} onChange={(e) => setStaple(e.target.checked)} />
        <span>{t("recipe.ingredientStaple")}</span>
      </label>
      {error && <p role="alert">{error}</p>}
      <div className="form-actions">
        <button type="button" className="button" disabled={busy || name.trim() === ""} onClick={() => void submit()}>
          {t("recipe.ingredientCreateSubmit")}
        </button>
        <button type="button" className="button button-outline" onClick={onCancel}>
          {t("recipe.cancel")}
        </button>
      </div>
    </fieldset>
  );
}
