import { useState } from "react";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { useI18n, type MessageKey } from "../i18n";
import { FOOD_TYPES, foodTypeKey, type FoodType } from "../recipes/food-types";
import { call } from "./call";
import { useLoaded } from "./use-loaded";

type Rules = components["schemas"]["MealMixRulesResponse"];

/** The food types that can have a rule: everything but Unknown. */
export const ruleFoodTypes: readonly FoodType[] = FOOD_TYPES.filter((type) => type !== "Unknown");

/** Minimums by food type; a type missing or 0 has no rule. */
export type Minimums = Partial<Record<FoodType, number>>;

export const toMinimums = (rules: Rules["rules"]): Minimums =>
  Object.fromEntries(rules.map((r) => [r.foodType, Number(r.minPerWeek)]));

/** The body for the API: only types with a minimum of 1 or more, in the order of the picker. */
export const toRules = (minimums: Minimums) =>
  ruleFoodTypes.flatMap((foodType) => {
    const minPerWeek = minimums[foodType] ?? 0;
    return minPerWeek > 0 ? [{ foodType, minPerWeek }] : [];
  });

export const sumOf = (minimums: Minimums) => ruleFoodTypes.reduce((sum, type) => sum + (minimums[type] ?? 0), 0);

/**
 * The household's weekly meal mix rules (T-090): "at least N dinners of food type X each week". Everyone sees them; Owner and Adult
 * change them (the API enforces the same with HouseholdAdult). The sum of the minimums cannot exceed the dinners of a week.
 */
export function MealMixRules({ householdId, canEdit }: { householdId: string; canEdit: boolean }) {
  const { t } = useI18n();
  const {
    data,
    error: loadError,
    setData,
  } = useLoaded<Rules>(householdId, () =>
    call(() => api.GET("/api/households/{householdId}/meal-mix-rules", { params: { path: { householdId } } })),
  );
  // What the user has changed and not saved yet; until then the saved rules are shown.
  const [draft, setDraft] = useState<Minimums | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const [saved, setSaved] = useState(false);

  const minimums = draft ?? (data ? toMinimums(data.rules) : {});
  const days = Number(data?.dinnerDaysPerWeek ?? 7);
  const total = sumOf(minimums);
  const change = (type: FoodType, delta: number) => {
    setSaved(false);
    setDraft({ ...minimums, [type]: Math.max(0, (minimums[type] ?? 0) + delta) });
  };

  const save = async () => {
    if (busy) return;
    setBusy(true);
    setError(null);
    const result = await call(() =>
      api.PUT("/api/households/{householdId}/meal-mix-rules", {
        params: { path: { householdId } },
        body: { rules: toRules(minimums) },
      }),
    );
    setBusy(false);
    if (!result.ok) setError(result.key);
    else if (result.data) {
      setData(result.data);
      setDraft(null);
      setSaved(true);
    }
  };

  const valueText = (value: number) => (value === 0 ? t("household.mix.none") : t("household.mix.atLeast", { count: value }));

  return (
    <div className="household-card" data-testid="meal-mix-rules">
      <h2>{t("household.mix.title")}</h2>
      <p>{t("household.mix.body", { days })}</p>
      {loadError && (
        <p role="alert" className="form-error">
          {t(loadError)}
        </p>
      )}
      {!data && !loadError && <p role="status">{t("household.mix.loading")}</p>}
      {data && (
        <>
          <ul className="household-list" data-testid="mix-list">
            {ruleFoodTypes.map((type) => {
              const value = minimums[type] ?? 0;
              const name = t(foodTypeKey(type));
              return (
                <li key={type} className="household-row" data-testid="mix-row" data-food-type={type}>
                  <span className="household-row-title">{name}</span>
                  {canEdit ? (
                    <div className="mix-stepper" role="group" aria-label={t("household.mix.minimumFor", { type: name })}>
                      <button
                        type="button"
                        className="mix-step"
                        disabled={busy || value <= 0}
                        aria-label={t("household.mix.fewer", { type: name })}
                        onClick={() => change(type, -1)}
                      >
                        <span aria-hidden="true">&minus;</span>
                      </button>
                      <span className="mix-value" data-testid="mix-value" aria-live="polite">
                        {valueText(value)}
                      </span>
                      <button
                        type="button"
                        className="mix-step"
                        disabled={busy || total >= days}
                        aria-label={t("household.mix.more", { type: name })}
                        onClick={() => change(type, 1)}
                      >
                        <span aria-hidden="true">+</span>
                      </button>
                    </div>
                  ) : (
                    <span data-testid="mix-value">{valueText(value)}</span>
                  )}
                </li>
              );
            })}
          </ul>
          <p className="household-note" data-testid="mix-total">
            {t("household.mix.total", { total, days })}
          </p>
        </>
      )}
      {error && (
        <p role="alert" className="form-error">
          {t(error)}
        </p>
      )}
      {saved && (
        <p role="status" data-testid="mix-saved">
          {t("household.mix.saved")}
        </p>
      )}
      {data && canEdit && (
        <button type="button" className="button" disabled={busy} onClick={() => void save()}>
          {t("household.mix.save")}
        </button>
      )}
      {data && !canEdit && <p className="household-note">{t("household.mix.adultsOnly")}</p>}
    </div>
  );
}
