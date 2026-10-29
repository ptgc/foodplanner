import { useEffect, useId, useState, type FormEvent } from "react";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { useI18n, type MessageKey } from "../i18n";
import { IngredientPicker } from "../recipes/ingredient-picker";
import type { IngredientChoice } from "../recipes/recipe-form";
import { call } from "./call";
import { useLoaded } from "./use-loaded";

type Weekly = components["schemas"]["WeeklyGroceryResponse"];
type Unit = components["schemas"]["UnitResponse"];

/** The most the API accepts for one household (WeeklyGrocery.MaxPerHousehold). */
export const maxWeeklyGroceries = 50;

/** "" is no amount; otherwise a positive number ("1,5" and "1.5" both work); null when what was typed is not one. */
export function parseAmount(text: string): number | null | "invalid" {
  if (text.trim() === "") return null;
  const n = Number(text.trim().replace(",", "."));
  return Number.isFinite(n) && n > 0 ? n : "invalid";
}

/** The body fields for what was typed: amount and unit together, or neither. */
function amountBody(text: string, unitId: string): { quantity: number | null; unitId: string | null } {
  const amount = parseAmount(text);
  return amount === null || amount === "invalid" ? { quantity: null, unitId: null } : { quantity: amount, unitId };
}

/**
 * The household's weekly groceries (T-092): regular items such as bread and milk that one button on the shopping list puts
 * on the week's list. Everyone sees the set; Owner and Adult change it (the API enforces the same with HouseholdAdult).
 */
export function WeeklyGroceries({ householdId, canEdit }: { householdId: string; canEdit: boolean }) {
  const { t, formatNumber } = useI18n();
  const [error, setError] = useState<MessageKey | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [units, setUnits] = useState<Unit[]>([]);
  const { data: items, error: loadError, reload } = useLoaded<Weekly[]>(householdId, () =>
    call(() => api.GET("/api/households/{householdId}/weekly-groceries", { params: { path: { householdId } } })),
  );

  useEffect(() => {
    let cancelled = false;
    void api
      .GET("/api/units")
      .then(({ data }) => {
        if (!cancelled && data) setUnits(data);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, []);

  const remove = async (item: Weekly) => {
    setError(null);
    const result = await call(() =>
      api.DELETE("/api/households/{householdId}/weekly-groceries/{id}", { params: { path: { householdId, id: item.id } } }),
    );
    if (result.ok) reload();
    else setError(result.key);
  };

  const quantityText = (item: Weekly) =>
    item.quantity === null || item.quantity === undefined
      ? ""
      : `${formatNumber(Number(item.quantity), { maximumFractionDigits: 2 })} ${item.unitCode ?? ""}`.trim();

  return (
    <div className="household-card" data-testid="weekly-groceries">
      <h2>{t("household.weekly.title")}</h2>
      <p>{t("household.weekly.body")}</p>
      {loadError && (
        <p role="alert" className="form-error">
          {t(loadError)}
        </p>
      )}
      {error && (
        <p role="alert" className="form-error">
          {t(error)}
        </p>
      )}
      {!items && !loadError && <p role="status">{t("household.weekly.loading")}</p>}
      {items && items.length === 0 && <p data-testid="weekly-empty">{t("household.weekly.empty")}</p>}
      {items && items.length > 0 && (
        <ul className="household-list" data-testid="weekly-list">
          {items.map((item) => (
            <li key={item.id} className="household-row" data-testid="weekly-row">
              {editing === item.id ? (
                <WeeklyAmountForm
                  householdId={householdId}
                  item={item}
                  units={units}
                  onDone={() => {
                    setEditing(null);
                    reload();
                  }}
                  onCancel={() => setEditing(null)}
                />
              ) : (
                <>
                  <div className="household-row-main">
                    <span className="household-row-title">{item.name}</span>
                    <span className="household-row-sub">{quantityText(item) || t("household.weekly.noAmount")}</span>
                  </div>
                  {canEdit && (
                    <div className="household-row-actions">
                      <button
                        type="button"
                        className="button button-outline"
                        aria-label={t("household.weekly.editFor", { name: item.name })}
                        onClick={() => {
                          setError(null);
                          setEditing(item.id);
                        }}
                      >
                        {t("household.weekly.edit")}
                      </button>
                      <button
                        type="button"
                        className="button button-outline"
                        aria-label={t("household.weekly.removeFor", { name: item.name })}
                        onClick={() => void remove(item)}
                      >
                        {t("household.weekly.remove")}
                      </button>
                    </div>
                  )}
                </>
              )}
            </li>
          ))}
        </ul>
      )}
      {canEdit ? (
        items && items.length >= maxWeeklyGroceries ? (
          <p className="household-note">{t("household.weekly.full", { max: maxWeeklyGroceries })}</p>
        ) : (
          <AddWeeklyForm householdId={householdId} units={units} onAdded={reload} />
        )
      ) : (
        <p className="household-note">{t("household.weekly.adultsOnly")}</p>
      )}
    </div>
  );
}

function UnitSelect({ id, units, value, onChange }: { id: string; units: Unit[]; value: string; onChange: (v: string) => void }) {
  const { t } = useI18n();
  return (
    <select id={id} value={value} onChange={(e) => onChange(e.target.value)}>
      <option value="">{t("household.weekly.noUnit")}</option>
      {units.map((u) => (
        <option key={u.id} value={u.id}>
          {u.code}
        </option>
      ))}
    </select>
  );
}

function AddWeeklyForm({ householdId, units, onAdded }: { householdId: string; units: Unit[]; onAdded: () => void }) {
  const { t } = useI18n();
  const pickerId = useId();
  const quantityId = useId();
  const unitId = useId();
  const [ingredient, setIngredient] = useState<IngredientChoice | null>(null);
  const [quantity, setQuantity] = useState("");
  const [unit, setUnit] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);

  const amount = parseAmount(quantity);
  // The API wants an amount and a unit together, or neither.
  const invalid = !ingredient || amount === "invalid" || (amount !== null && !unit) || (amount === null && unit !== "");

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (invalid || busy || !ingredient) return;
    setBusy(true);
    setError(null);
    const result = await call(() =>
      api.POST("/api/households/{householdId}/weekly-groceries", {
        params: { path: { householdId } },
        body: { ingredientId: ingredient.id, ...amountBody(quantity, unit) },
      }),
    );
    setBusy(false);
    if (result.ok) {
      setIngredient(null);
      setQuantity("");
      setUnit("");
      onAdded();
    } else setError(result.key);
  };

  return (
    <form className="household-weekly-add" onSubmit={(e) => void submit(e)}>
      <h3>{t("household.weekly.addTitle")}</h3>
      <IngredientPicker householdId={householdId} value={ingredient} onChange={setIngredient} id={pickerId} />
      <div className="household-inline">
        <div className="field">
          <label htmlFor={quantityId}>{t("household.weekly.quantity")}</label>
          <input id={quantityId} inputMode="decimal" value={quantity} onChange={(e) => setQuantity(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor={unitId}>{t("household.weekly.unit")}</label>
          <UnitSelect id={unitId} units={units} value={unit} onChange={setUnit} />
        </div>
      </div>
      {error && (
        <p role="alert" className="form-error">
          {t(error)}
        </p>
      )}
      <button type="submit" className="button" disabled={invalid || busy}>
        {t("household.weekly.add")}
      </button>
    </form>
  );
}

function WeeklyAmountForm({
  householdId,
  item,
  units,
  onDone,
  onCancel,
}: {
  householdId: string;
  item: Weekly;
  units: Unit[];
  onDone: () => void;
  onCancel: () => void;
}) {
  const { t } = useI18n();
  const quantityId = useId();
  const unitId = useId();
  const [quantity, setQuantity] = useState(item.quantity === null || item.quantity === undefined ? "" : String(item.quantity));
  const [unit, setUnit] = useState(item.unitId ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const amount = parseAmount(quantity);
  const invalid = amount === "invalid" || (amount !== null && !unit) || (amount === null && unit !== "");

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (invalid || busy) return;
    setBusy(true);
    setError(null);
    const result = await call(() =>
      api.PUT("/api/households/{householdId}/weekly-groceries/{id}", {
        params: { path: { householdId, id: item.id } },
        body: amountBody(quantity, unit),
      }),
    );
    setBusy(false);
    if (result.ok) onDone();
    else setError(result.key);
  };

  return (
    <form className="household-weekly-edit" onSubmit={(e) => void submit(e)}>
      <strong>{item.name}</strong>
      <div className="household-inline">
        <div className="field">
          <label htmlFor={quantityId}>{t("household.weekly.quantity")}</label>
          <input id={quantityId} inputMode="decimal" value={quantity} onChange={(e) => setQuantity(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor={unitId}>{t("household.weekly.unit")}</label>
          <UnitSelect id={unitId} units={units} value={unit} onChange={setUnit} />
        </div>
      </div>
      {error && (
        <p role="alert" className="form-error">
          {t(error)}
        </p>
      )}
      <div className="household-actions">
        <button type="submit" className="button" disabled={invalid || busy}>
          {t("household.weekly.save")}
        </button>
        <button type="button" className="button button-outline" onClick={onCancel}>
          {t("household.weekly.cancel")}
        </button>
      </div>
    </form>
  );
}
