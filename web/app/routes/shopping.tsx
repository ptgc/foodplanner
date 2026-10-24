import { useEffect, useId, useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { useSession } from "../session";
import { t } from "../shopping/strings";
import { useShoppingList, type ActionResult } from "../shopping/use-shopping-list";
import { addWeeks, isMonday, isoWeek, mondayOf, weekRange } from "../shopping/week";

type Item = components["schemas"]["ShoppingListItemResponse"];
type Unit = components["schemas"]["UnitResponse"];

export function formatQuantities(item: Item): string {
  if (item.quantities.length === 0) return "";
  return item.quantities
    .map((q) => {
      if (q.amount === null || q.amount === undefined) return t.toTaste;
      const n = Math.round(Number(q.amount) * 100) / 100;
      return q.unitCode ? `${n} ${q.unitCode}` : String(n);
    })
    .join(" + ");
}

/** Unchecked first, checked last; the server's (nb-NO) order is kept inside each group. */
export function checkedLast(items: Item[]): Item[] {
  return [...items.filter((i) => !i.checked), ...items.filter((i) => i.checked)];
}

export default function Shopping() {
  const { session } = useSession();
  if (session.status !== "signedIn") return null;
  if (!session.activeHouseholdId) return <p>{t.noHousehold}</p>;
  // Keyed so switching household tears down the live connection and starts a fresh one.
  return <ShoppingScreen key={session.activeHouseholdId} householdId={session.activeHouseholdId} />;
}

function ShoppingScreen({ householdId }: { householdId: string }) {
  const [params, setParams] = useSearchParams();
  const requested = params.get("week");
  const weekStart = isMonday(requested) ? requested : mondayOf(new Date());
  const model = useShoppingList(householdId, weekStart);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const { week, year } = isoWeek(weekStart);

  const goToWeek = (monday: string) => {
    setMessage(null);
    setParams({ week: monday }, { replace: true });
  };

  const report = (r: ActionResult) => {
    setMessage(r === "ok" ? null : r === "offline" ? t.needsOnline : t.actionFailed);
  };

  const generate = async () => {
    setBusy(true);
    try {
      report(await model.generate());
    } finally {
      setBusy(false);
    }
  };

  if (model.forbidden) {
    return (
      <section className="shop">
        <h1>{t.forbiddenTitle}</h1>
        <p>{t.forbiddenBody}</p>
        {/* A full navigation: the household list is read again when the app loads. */}
        <a className="button shop-link" href="/">
          {t.forbiddenLink}
        </a>
      </section>
    );
  }

  const list = model.list;
  const empty = !!list && list.main.length === 0 && list.checkYouHave.length === 0;

  return (
    <section className="shop" aria-labelledby="shop-title">
      <h1 id="shop-title">{t.title}</h1>

      <div className="shop-weeks">
        <button type="button" className="button" onClick={() => goToWeek(addWeeks(weekStart, -1))}>
          <span aria-hidden="true">&lsaquo;</span>
          <span className="visually-hidden">{t.prevWeek}</span>
        </button>
        <p className="shop-week" aria-live="polite" data-testid="shop-week">
          <strong>{t.week(week, year)}</strong>
          <span>{weekRange(weekStart)}</span>
        </p>
        <button type="button" className="button" onClick={() => goToWeek(addWeeks(weekStart, 1))}>
          <span aria-hidden="true">&rsaquo;</span>
          <span className="visually-hidden">{t.nextWeek}</span>
        </button>
      </div>

      {!model.online && (
        <p role="status" className="shop-banner shop-offline" data-testid="offline-banner">
          {t.offlineBanner}
        </p>
      )}
      {model.online && model.fromCache && (
        <p role="status" className="shop-banner" data-testid="cached-banner">
          {t.cachedBanner}
        </p>
      )}
      {model.pending > 0 && (
        <p role="status" className="shop-banner shop-pending" data-testid="pending-sync">
          {t.pending(model.pending)}
        </p>
      )}

      <button type="button" className="button shop-generate" disabled={busy || !model.online} onClick={() => void generate()}>
        {busy ? t.generating : t.generate}
      </button>
      {message && (
        <p role="alert" className="form-error">
          {message}
        </p>
      )}

      {model.loading && <p role="status">{t.loading}</p>}
      {!list && model.loadError === "unauthorized" && (
        <p role="alert" className="form-error">
          {t.signInAgain} <Link to="/login">Sign in</Link>
        </p>
      )}
      {!list && model.loadError && model.loadError !== "unauthorized" && (
        <p role="alert" className="form-error">
          {model.loadError === "network" ? t.noCache : t.loadFailed}
        </p>
      )}

      {list && (
        <>
          {empty && <p>{t.emptyList}</p>}
          <ItemSection
            title={t.sectionMain}
            items={list.main}
            onToggle={model.toggle}
            onRemove={async (id) => report(await model.removeItem(id))}
            canRemove={model.online}
          />
          <ItemSection
            title={t.sectionStaples}
            items={list.checkYouHave}
            onToggle={model.toggle}
            onRemove={async (id) => report(await model.removeItem(id))}
            canRemove={model.online}
          />
        </>
      )}

      <AddItemForm
        disabled={!model.online}
        onAdd={async (label, quantity, unitId) => report(await model.addItem(label, quantity, unitId))}
      />
    </section>
  );
}

function ItemSection(props: {
  title: string;
  items: Item[];
  onToggle: (id: string, checked: boolean) => void;
  onRemove: (id: string) => Promise<void>;
  canRemove: boolean;
}) {
  const headingId = useId();
  if (props.items.length === 0) return null;
  return (
    <section aria-labelledby={headingId} className="shop-section">
      <h2 id={headingId}>{props.title}</h2>
      <ul className="shop-items">
        {checkedLast(props.items).map((item) => (
          <ItemRow key={item.id} item={item} {...props} />
        ))}
      </ul>
    </section>
  );
}

function ItemRow({
  item,
  onToggle,
  onRemove,
  canRemove,
}: {
  item: Item;
  onToggle: (id: string, checked: boolean) => void;
  onRemove: (id: string) => Promise<void>;
  canRemove: boolean;
}) {
  const nameId = useId();
  const detailId = useId();
  const quantity = formatQuantities(item);
  const from = item.recipes.map((r) => r.title).join(", ");
  const hasDetail = !!quantity || !!from || item.quantityChangedSinceChecked || item.isManual;
  return (
    <li className={`shop-row${item.checked ? " is-checked" : ""}`} data-testid="shop-item">
      {/* The whole label is the touch target (>= 56px); the native checkbox keeps keyboard and screen reader support. */}
      <label className="shop-check">
        <input
          type="checkbox"
          checked={item.checked}
          onChange={() => onToggle(item.id, item.checked)}
          aria-labelledby={nameId}
          aria-describedby={hasDetail ? detailId : undefined}
        />
        <span className="shop-text">
          <span id={nameId} className="shop-name">
            {item.name}
          </span>
          {hasDetail && (
            <span id={detailId} className="shop-detail">
              {quantity && <span className="shop-qty">{quantity}</span>}
              {from && <span>{t.fromRecipes(from)}</span>}
              {item.isManual && <span>{t.manual}</span>}
              {item.quantityChangedSinceChecked && <span className="shop-changed">{t.quantityChanged}</span>}
            </span>
          )}
        </span>
      </label>
      {item.isManual && (
        <button
          type="button"
          className="button shop-remove"
          disabled={!canRemove}
          aria-label={t.remove(item.name)}
          onClick={() => void onRemove(item.id)}
        >
          <span aria-hidden="true">&times;</span>
        </button>
      )}
    </li>
  );
}

function AddItemForm({
  disabled,
  onAdd,
}: {
  disabled: boolean;
  onAdd: (label: string, quantity: number | null, unitId: string | null) => Promise<void>;
}) {
  const [label, setLabel] = useState("");
  const [quantity, setQuantity] = useState("");
  const [unitId, setUnitId] = useState("");
  const [units, setUnits] = useState<Unit[]>([]);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let cancelled = false;
    void api
      .GET("/api/units")
      .then(({ data }) => {
        if (!cancelled && data) setUnits(data);
      })
      .catch(() => undefined); // offline: the form still works without a unit
    return () => {
      cancelled = true;
    };
  }, []);

  const amount = quantity.trim() === "" ? null : Number(quantity.replace(",", "."));
  // The API wants amount and unit together, or neither.
  const invalid = label.trim() === "" || (amount !== null && (!Number.isFinite(amount) || amount <= 0 || !unitId));

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (invalid || busy) return;
    setBusy(true);
    try {
      await onAdd(label.trim(), amount, amount === null ? null : unitId);
      setLabel("");
      setQuantity("");
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="shop-add" onSubmit={(e) => void submit(e)}>
      <h2>{t.addTitle}</h2>
      <div className="field">
        <label htmlFor="shop-add-label">{t.addLabel}</label>
        <input id="shop-add-label" value={label} maxLength={100} onChange={(e) => setLabel(e.target.value)} />
      </div>
      <div className="shop-add-qty">
        <div className="field">
          <label htmlFor="shop-add-qty">{t.addQuantity}</label>
          <input id="shop-add-qty" inputMode="decimal" value={quantity} onChange={(e) => setQuantity(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor="shop-add-unit">{t.addUnit}</label>
          <select id="shop-add-unit" value={unitId} onChange={(e) => setUnitId(e.target.value)}>
            <option value="">{t.addNoUnit}</option>
            {units.map((u) => (
              <option key={u.id} value={u.id}>
                {u.code}
              </option>
            ))}
          </select>
        </div>
      </div>
      <button type="submit" className="button" disabled={disabled || busy || invalid}>
        {t.add}
      </button>
      {disabled && <p className="shop-hint">{t.needsOnline}</p>}
    </form>
  );
}
