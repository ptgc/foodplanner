import { useEffect, useId, useState, type FormEvent } from "react";
import { Link } from "react-router";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { call } from "../household/call";
import { ConfirmDialog } from "../household/confirm-dialog";
import { powers } from "../household/roles";
import { useLoaded } from "../household/use-loaded";
import { parseAmount } from "../household/weekly-groceries";
import { useI18n, type MessageKey } from "../i18n";
import { IngredientPicker } from "../recipes/ingredient-picker";
import type { IngredientChoice } from "../recipes/recipe-form";
import { useSession } from "../session";

type Entry = components["schemas"]["PantryItemResponse"];
type Unit = components["schemas"]["UnitResponse"];

/** The end of the chosen day, local time, as the instant the entry expires; null when no day is chosen (no expiry). */
export function expiryFromDate(date: string): string | null {
  if (!date) return null;
  const end = new Date(`${date}T23:59:59`);
  return Number.isNaN(end.getTime()) ? null : end.toISOString();
}

/** The local day (YYYY-MM-DD) of an expiry instant, for the date field. */
export function dateFromExpiry(iso: string | null | undefined): string {
  if (!iso) return "";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

const today = () => dateFromExpiry(new Date().toISOString());

/** The household's pantry (T-093): everything it has at home, with an optional amount and expiry. */
export default function Pantry() {
  const { t } = useI18n();
  const { session } = useSession();
  if (session.status !== "signedIn") return null;
  const household = session.households.find((h) => h.id === session.activeHouseholdId);
  if (!household) return <p>{t("shopping.noHousehold")}</p>;
  return <PantryScreen key={household.id} householdId={household.id} canClear={powers(household.role).weekly} />;
}

function PantryScreen({ householdId, canClear }: { householdId: string; canClear: boolean }) {
  const { t, formatNumber } = useI18n();
  const [error, setError] = useState<MessageKey | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [clearing, setClearing] = useState(false);
  const [clearBusy, setClearBusy] = useState(false);
  const [units, setUnits] = useState<Unit[]>([]);
  const { data: entries, error: loadError, reload } = useLoaded<Entry[]>(householdId, () =>
    call(() =>
      api.GET("/api/households/{householdId}/pantry", { params: { path: { householdId }, query: { includeExpired: true } } }),
    ),
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

  const dateText = (iso: string) => new Intl.DateTimeFormat(undefined, { dateStyle: "medium" }).format(new Date(iso));

  const amountText = (entry: Entry) =>
    entry.quantity === null || entry.quantity === undefined
      ? t("pantry.noAmount")
      : `${formatNumber(Number(entry.quantity), { maximumFractionDigits: 2 })} ${entry.unitCode ?? ""}`.trim();

  const remove = async (entry: Entry) => {
    setError(null);
    const result = await call(() =>
      api.DELETE("/api/households/{householdId}/pantry/{ingredientId}", { params: { path: { householdId, ingredientId: entry.ingredientId } } }),
    );
    if (result.ok) reload();
    else setError(result.key);
  };

  const clear = async () => {
    setClearBusy(true);
    setError(null);
    const result = await call(() => api.DELETE("/api/households/{householdId}/pantry", { params: { path: { householdId } } }));
    setClearBusy(false);
    setClearing(false);
    if (result.ok) reload();
    else setError(result.key);
  };

  return (
    <section className="household pantry" aria-labelledby="pantry-title" data-testid="pantry-screen">
      <h1 id="pantry-title">{t("pantry.title")}</h1>
      <p>{t("pantry.body")}</p>
      <p>
        <Link to="/shopping">{t("pantry.back")}</Link>
      </p>
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
      <div className="household-card">
        {!entries && !loadError && <p role="status">{t("pantry.loading")}</p>}
        {entries && entries.length === 0 && <p data-testid="pantry-empty">{t("pantry.empty")}</p>}
        {entries && entries.length > 0 && (
          <ul className="household-list" data-testid="pantry-list">
            {entries.map((entry) => (
              <li key={entry.ingredientId} className="household-row" data-testid="pantry-row">
                {editing === entry.ingredientId ? (
                  <EntryForm
                    householdId={householdId}
                    ingredientId={entry.ingredientId}
                    name={entry.name}
                    initial={entry}
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
                      <span className="household-row-title">{entry.name}</span>
                      <span className="household-row-sub" data-testid="pantry-amount">
                        {amountText(entry)}
                      </span>
                      {entry.expiresAt && (
                        <span className="household-row-sub">
                          {entry.isExpired ? t("pantry.expired", { date: dateText(entry.expiresAt) }) : t("pantry.expires", { date: dateText(entry.expiresAt) })}
                        </span>
                      )}
                    </div>
                    <div className="household-row-actions">
                      <button
                        type="button"
                        className="button button-outline"
                        aria-label={t("pantry.editFor", { name: entry.name })}
                        onClick={() => {
                          setError(null);
                          setEditing(entry.ingredientId);
                        }}
                      >
                        {t("pantry.edit")}
                      </button>
                      <button
                        type="button"
                        className="button button-outline"
                        aria-label={t("pantry.removeFor", { name: entry.name })}
                        onClick={() => void remove(entry)}
                      >
                        {t("pantry.remove")}
                      </button>
                    </div>
                  </>
                )}
              </li>
            ))}
          </ul>
        )}
        {canClear ? (
          entries &&
          entries.length > 0 && (
            <button type="button" className="button button-outline pantry-clear" onClick={() => setClearing(true)}>
              {t("pantry.clear")}
            </button>
          )
        ) : (
          <p className="household-note">{t("pantry.adultsOnlyClear")}</p>
        )}
      </div>
      <AddForm householdId={householdId} units={units} onAdded={reload} />
      {clearing && (
        <ConfirmDialog
          title={t("pantry.clearTitle")}
          body={t("pantry.clearBody")}
          confirmLabel={t("pantry.clearConfirm")}
          busy={clearBusy}
          error={null}
          onConfirm={() => void clear()}
          onCancel={() => setClearing(false)}
        />
      )}
    </section>
  );
}

function UnitSelect({ id, units, value, onChange }: { id: string; units: Unit[]; value: string; onChange: (v: string) => void }) {
  const { t } = useI18n();
  return (
    <select id={id} value={value} onChange={(e) => onChange(e.target.value)}>
      <option value="">{t("pantry.noUnit")}</option>
      {units.map((u) => (
        <option key={u.id} value={u.id}>
          {u.code}
        </option>
      ))}
    </select>
  );
}

/** Amount, unit and expiry fields shared by "add" and "edit". The API takes amount and unit together, or neither. */
function useEntryFields(initial?: Entry) {
  const [quantity, setQuantity] = useState(initial?.quantity === null || initial?.quantity === undefined ? "" : String(initial.quantity));
  const [unit, setUnit] = useState(initial?.unitId ?? "");
  const [expiry, setExpiry] = useState(dateFromExpiry(initial?.isExpired ? null : initial?.expiresAt)); // an expired day cannot be saved again
  const amount = parseAmount(quantity);
  const invalid = amount === "invalid" || (amount !== null && !unit) || (amount === null && unit !== "");
  const body = {
    expiresAt: expiryFromDate(expiry),
    quantity: amount === null || amount === "invalid" ? null : amount,
    unitId: amount === null || amount === "invalid" ? null : unit,
  };
  const reset = () => {
    setQuantity("");
    setUnit("");
    setExpiry("");
  };
  return { quantity, setQuantity, unit, setUnit, expiry, setExpiry, invalid, body, reset };
}

function Fields({ fields, units }: { fields: ReturnType<typeof useEntryFields>; units: Unit[] }) {
  const { t } = useI18n();
  const quantityId = useId();
  const unitId = useId();
  const expiryId = useId();
  return (
    <>
      <div className="household-inline">
        <div className="field">
          <label htmlFor={quantityId}>{t("pantry.amount")}</label>
          <input id={quantityId} inputMode="decimal" value={fields.quantity} onChange={(e) => fields.setQuantity(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor={unitId}>{t("pantry.unit")}</label>
          <UnitSelect id={unitId} units={units} value={fields.unit} onChange={fields.setUnit} />
        </div>
      </div>
      <div className="field">
        <label htmlFor={expiryId}>{t("pantry.expiry")}</label>
        <input id={expiryId} type="date" min={today()} value={fields.expiry} onChange={(e) => fields.setExpiry(e.target.value)} />
      </div>
    </>
  );
}

function save(householdId: string, ingredientId: string, body: ReturnType<typeof useEntryFields>["body"]) {
  return call(() =>
    api.PUT("/api/households/{householdId}/pantry/{ingredientId}", { params: { path: { householdId, ingredientId } }, body }),
  );
}

function AddForm({ householdId, units, onAdded }: { householdId: string; units: Unit[]; onAdded: () => void }) {
  const { t } = useI18n();
  const pickerId = useId();
  const fields = useEntryFields();
  const [ingredient, setIngredient] = useState<IngredientChoice | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const invalid = !ingredient || fields.invalid;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (invalid || busy || !ingredient) return;
    setBusy(true);
    setError(null);
    const result = await save(householdId, ingredient.id, fields.body);
    setBusy(false);
    if (result.ok) {
      setIngredient(null);
      fields.reset();
      onAdded();
    } else setError(result.key);
  };

  return (
    <form className="household-card pantry-add" onSubmit={(e) => void submit(e)}>
      <h2>{t("pantry.addTitle")}</h2>
      <IngredientPicker householdId={householdId} value={ingredient} onChange={setIngredient} id={pickerId} />
      <Fields fields={fields} units={units} />
      {error && (
        <p role="alert" className="form-error">
          {t(error)}
        </p>
      )}
      <button type="submit" className="button" disabled={invalid || busy}>
        {t("pantry.add")}
      </button>
    </form>
  );
}

function EntryForm({
  householdId,
  ingredientId,
  name,
  initial,
  units,
  onDone,
  onCancel,
}: {
  householdId: string;
  ingredientId: string;
  name: string;
  initial: Entry;
  units: Unit[];
  onDone: () => void;
  onCancel: () => void;
}) {
  const { t } = useI18n();
  const fields = useEntryFields(initial);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (fields.invalid || busy) return;
    setBusy(true);
    setError(null);
    const result = await save(householdId, ingredientId, fields.body);
    setBusy(false);
    if (result.ok) onDone();
    else setError(result.key);
  };

  return (
    <form className="household-weekly-edit" onSubmit={(e) => void submit(e)}>
      <strong>{name}</strong>
      <Fields fields={fields} units={units} />
      {error && (
        <p role="alert" className="form-error">
          {t(error)}
        </p>
      )}
      <div className="household-actions">
        <button type="submit" className="button" disabled={fields.invalid || busy}>
          {t("pantry.save")}
        </button>
        <button type="button" className="button button-outline" onClick={onCancel}>
          {t("pantry.cancel")}
        </button>
      </div>
    </form>
  );
}
