import { useEffect, useId, useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { useSession } from "../session";
import { useI18n, type I18n } from "../i18n";
import { useShoppingList, type ActionResult, type PantryItem } from "../shopping/use-shopping-list";
import { copyText, fetchExport, shareText } from "../shopping/export";
import { StorePicker } from "../shopping/store-picker";
import { groupByStoreSection, storeSectionKey } from "../shopping/store-sections";
import { addWeeks, isMonday, isoWeek, mondayOf, weekRange } from "../shopping/week";

type Item = components["schemas"]["ShoppingListItemResponse"];
type Unit = components["schemas"]["UnitResponse"];

export function formatQuantities(item: Item, i18n: Pick<I18n, "t" | "formatNumber">): string {
  // Optional arrays are treated as empty when missing (an old or partial payload must not crash the page, T-088).
  const quantities = item.quantities ?? [];
  if (quantities.length === 0) return "";
  return quantities
    .map((q) => {
      if (q.amount === null || q.amount === undefined) return i18n.t("shopping.toTaste");
      // Intl groups and separates by locale: 1.5 in en, 1,5 in nb.
      const n = i18n.formatNumber(Number(q.amount), { maximumFractionDigits: 2 });
      return q.unitCode ? `${n} ${q.unitCode}` : n;
    })
    .join(" + ");
}

function formatQuantity(q: { amount?: number | string | null; unitCode?: string | null } | null | undefined, i18n: Pick<I18n, "formatNumber">): string | null {
  if (!q || q.amount === null || q.amount === undefined) return null;
  const n = i18n.formatNumber(Number(q.amount), { maximumFractionDigits: 2 });
  return q.unitCode ? `${n} ${q.unitCode}` : n;
}

/**
 * The pantry note of an item (T-093): "300 g at home (need 200 g)" for a covered item under "Check you have", "100 g at home"
 * for a partly covered one (its quantity is what is left to buy), "At home, check there are enough" when the pantry has no
 * amount or another dimension. Null when the pantry has none of it.
 */
export function formatAtHome(item: Item, i18n: Pick<I18n, "t" | "formatNumber">): string | null {
  if (!item.atHome) return null;
  const have = formatQuantity(item.atHome.quantity, i18n);
  // A covered item keeps the quantity the plan needs; on the main list the quantity is what is still to buy.
  const need = item.section === "CheckYouHave" ? formatQuantities(item, i18n) : "";
  if (have) return need ? i18n.t("shopping.atHomeNeed", { have, need }) : i18n.t("shopping.atHome", { have });
  return need ? i18n.t("shopping.atHomeSomeNeed", { need }) : i18n.t("shopping.atHomeSome");
}

/** Unchecked first, checked last; the server's (nb-NO) order is kept inside each group. */
export function checkedLast(items: Item[]): Item[] {
  return [...items.filter((i) => !i.checked), ...items.filter((i) => i.checked)];
}

export default function Shopping() {
  const { t } = useI18n();
  const { session } = useSession();
  if (session.status !== "signedIn") return null;
  if (!session.activeHouseholdId) return <p>{t("shopping.noHousehold")}</p>;
  // Keyed so switching household tears down the live connection and starts a fresh one.
  return <ShoppingScreen key={session.activeHouseholdId} householdId={session.activeHouseholdId} />;
}

function ShoppingScreen({ householdId }: { householdId: string }) {
  const { t, formatDayMonth, formatNumber } = useI18n();
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
    setMessage(r === "ok" ? null : r === "offline" ? t("shopping.needsOnline") : t("shopping.actionFailed"));
  };

  const generate = async () => {
    setBusy(true);
    try {
      report(await model.generate());
    } finally {
      setBusy(false);
    }
  };

  const addWeekly = async () => {
    setBusy(true);
    try {
      report(await model.addWeekly());
    } finally {
      setBusy(false);
    }
  };

  if (model.forbidden) {
    return (
      <section className="shop">
        <h1>{t("shopping.forbiddenTitle")}</h1>
        <p>{t("shopping.forbiddenBody")}</p>
        {/* A full navigation: the household list is read again when the app loads. */}
        <a className="button shop-link" href="/">
          {t("shopping.chooseHousehold")}
        </a>
      </section>
    );
  }

  const list = model.list;
  const empty = !!list && list.main.length === 0 && list.checkYouHave.length === 0;

  return (
    <section className="shop" aria-labelledby="shop-title">
      <h1 id="shop-title">{t("shopping.title")}</h1>

      <div className="shop-weeks">
        <button type="button" className="button" onClick={() => goToWeek(addWeeks(weekStart, -1))}>
          <span aria-hidden="true">&lsaquo;</span>
          <span className="visually-hidden">{t("shopping.previousWeek")}</span>
        </button>
        <p className="shop-week" aria-live="polite" data-testid="shop-week">
          <strong>
            {t("shopping.week", {
              week: formatNumber(week, { useGrouping: false }),
              year: formatNumber(year, { useGrouping: false }),
            })}
          </strong>
          <span>{weekRange(weekStart, formatDayMonth)}</span>
        </p>
        <button type="button" className="button" onClick={() => goToWeek(addWeeks(weekStart, 1))}>
          <span aria-hidden="true">&rsaquo;</span>
          <span className="visually-hidden">{t("shopping.nextWeek")}</span>
        </button>
      </div>

      {!model.online && (
        <p role="status" className="shop-banner shop-offline" data-testid="offline-banner">
          {t("shopping.offlineBanner")}
        </p>
      )}
      {model.online && model.fromCache && (
        <p role="status" className="shop-banner" data-testid="cached-banner">
          {t("shopping.cachedBanner")}
        </p>
      )}
      {model.pending > 0 && (
        <p role="status" className="shop-banner shop-pending" data-testid="pending-sync">
          {t("shopping.pending", { count: model.pending })}
        </p>
      )}

      <button type="button" className="button shop-generate" disabled={busy || !model.online} onClick={() => void generate()}>
        {busy ? t("shopping.generating") : t("shopping.generate")}
      </button>
      <button
        type="button"
        className="button shop-weekly"
        data-testid="add-weekly"
        disabled={busy || !model.online || !model.weeklyCount}
        onClick={() => void addWeekly()}
      >
        {t("shopping.addWeekly")}
      </button>
      {model.weeklyCount === 0 && (
        <p className="shop-hint" data-testid="weekly-empty-hint">
          {t("shopping.weeklyEmpty")} <Link to="/household">{t("shopping.weeklyEmptyLink")}</Link>
        </p>
      )}
      <StorePicker householdId={householdId} online={model.online} onChoose={model.chooseStore} />
      <ExportBar householdId={householdId} weekStart={weekStart} online={model.online} />
      {message && (
        <p role="alert" className="form-error">
          {message}
        </p>
      )}

      {model.loading && <p role="status">{t("shopping.loading")}</p>}
      {!list && model.loadError === "unauthorized" && (
        <p role="alert" className="form-error">
          {t("shopping.signInAgain")} <Link to="/login">{t("common.signIn")}</Link>
        </p>
      )}
      {!list && model.loadError && model.loadError !== "unauthorized" && (
        <p role="alert" className="form-error">
          {model.loadError === "network" ? t("shopping.noCache") : t("shopping.loadFailed")}
        </p>
      )}

      {list && (
        <>
          {empty && <p>{t("shopping.emptyList")}</p>}
          <ItemSection
            title={t("shopping.sectionMain")}
            items={list.main}
            bySection
            onToggle={model.toggle}
            onRemove={async (id) => report(await model.removeItem(id))}
            canRemove={model.online}
            onWeHaveThis={async (ingredientId, quantity, unitId) => report(await model.haveThis(ingredientId, quantity, unitId))}
          />
          <ItemSection
            title={t("shopping.sectionStaples")}
            items={list.checkYouHave}
            onToggle={model.toggle}
            onRemove={async (id) => report(await model.removeItem(id))}
            canRemove={model.online}
            onHaveIt={async (ingredientId) => report(await model.confirmStaple(ingredientId))}
          />
        </>
      )}

      <PantrySummary
        items={model.pantry}
        canChange={model.online}
        onRemove={async (ingredientId) => report(await model.unconfirmStaple(ingredientId))}
      />

      <Link className="button button-outline shop-pantry-link" to="/pantry" data-testid="pantry-link">
        {t("shopping.pantryLink")}
      </Link>

      <AddItemForm
        disabled={!model.online}
        onAdd={async (label, quantity, unitId) => report(await model.addItem(label, quantity, unitId))}
      />
    </section>
  );
}

/** Share and Copy for the week on screen. The text is formatted by the API, so both need it to be reachable. */
function ExportBar({ householdId, weekStart, online }: { householdId: string; weekStart: string; online: boolean }) {
  const { t } = useI18n();
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [manual, setManual] = useState<string | null>(null);

  const run = async (mode: "share" | "copy") => {
    setBusy(true);
    setStatus(null);
    setError(null);
    setManual(null);
    try {
      const payload = await fetchExport(householdId, weekStart);
      if (payload === "failed") {
        setError(t("shopping.exportFailed"));
        return;
      }
      if (mode === "share") {
        const shared = await shareText(payload);
        if (shared !== "unavailable") return; // shared, or the user cancelled: nothing more to do
      }
      if (await copyText(payload.text)) setStatus(t("shopping.copied"));
      else setManual(payload.text);
    } catch {
      setError(t("shopping.exportFailed"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="shop-export">
      <div className="shop-export-buttons">
        <button type="button" className="button" disabled={busy || !online} onClick={() => void run("share")}>
          {t("shopping.share")}
        </button>
        <button type="button" className="button" disabled={busy || !online} onClick={() => void run("copy")}>
          {t("shopping.copy")}
        </button>
      </div>
      {!online && <p className="shop-hint">{t("shopping.exportNeedsOnline")}</p>}
      <p role="status" aria-live="polite" className="shop-toast" data-testid="copy-toast">
        {status}
      </p>
      {error && (
        <p role="alert" className="form-error">
          {error}
        </p>
      )}
      {manual !== null && (
        <div className="field">
          <label htmlFor="shop-export-text">{t("shopping.copyManually")}</label>
          <textarea id="shop-export-text" readOnly rows={8} value={manual} onFocus={(e) => e.currentTarget.select()} />
        </div>
      )}
    </div>
  );
}

function ItemSection(props: {
  title: string;
  items: Item[];
  onToggle: (id: string, checked: boolean) => void;
  onRemove: (id: string) => Promise<void>;
  canRemove: boolean;
  /** "I have it" for a staple (only the check-you-have section passes it). */
  onHaveIt?: (ingredientId: string) => Promise<void>;
  /** "We have this" with an optional amount (the to-buy list, T-093). */
  onWeHaveThis?: (ingredientId: string, quantity: number | null, unitId: string | null) => Promise<void>;
  /** Group by store section with sub-headings (the "to buy" list). */
  bySection?: boolean;
}) {
  const { t } = useI18n();
  const headingId = useId();
  if (props.items.length === 0) return null;
  return (
    <section aria-labelledby={headingId} className="shop-section">
      <h2 id={headingId}>{props.title}</h2>
      {props.bySection ? (
        // The list is in store-walking order (T-085): one sub-heading per store section.
        groupByStoreSection(props.items).map((group) => (
          <div key={group.section} className="shop-group" data-testid="shop-group" data-section={group.section}>
            <h3 className="shop-group-title">{t(storeSectionKey(group.section))}</h3>
            <ul className="shop-items">
              {checkedLast(group.items).map((item) => (
                <ItemRow key={item.id} item={item} {...props} />
              ))}
            </ul>
          </div>
        ))
      ) : (
        <ul className="shop-items">
          {checkedLast(props.items).map((item) => (
            <ItemRow key={item.id} item={item} {...props} />
          ))}
        </ul>
      )}
    </section>
  );
}

function ItemRow({
  item,
  onToggle,
  onRemove,
  canRemove,
  onHaveIt,
  onWeHaveThis,
}: {
  item: Item;
  onToggle: (id: string, checked: boolean) => void;
  onRemove: (id: string) => Promise<void>;
  canRemove: boolean;
  onHaveIt?: (ingredientId: string) => Promise<void>;
  onWeHaveThis?: (ingredientId: string, quantity: number | null, unitId: string | null) => Promise<void>;
}) {
  const i18n = useI18n();
  const { t } = i18n;
  const nameId = useId();
  const detailId = useId();
  const [haveOpen, setHaveOpen] = useState(false);
  const atHome = formatAtHome(item, i18n);
  // A covered item's quantity is part of the pantry note ("need 200 g"), so it is not shown twice.
  const quantity = atHome && item.section === "CheckYouHave" ? "" : formatQuantities(item, i18n);
  const from = (item.recipes ?? []).map((r) => r.title).join(", ");
  const alternatives = (item.alternatives ?? []).map((a) => a.ingredientName).join(", ");
  const hasDetail = !!quantity || !!atHome || !!from || !!alternatives || item.quantityChangedSinceChecked || item.isManual || item.isWeekly;
  // "I have it" is for a staple or "to taste" line the pantry does not hold yet; an item the pantry holds is changed on the pantry screen.
  const canHaveIt = !!onHaveIt && !!item.ingredientId && !item.isManual && !item.atHome;
  const canWeHaveThis = !!onWeHaveThis && !!item.ingredientId && !item.isManual;
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
              {atHome && (
                <span className="shop-at-home" data-testid="shop-at-home">
                  {atHome}
                </span>
              )}
              {alternatives && <span data-testid="shop-alternatives">{t("shopping.or", { names: alternatives })}</span>}
              {from && <span>{t("shopping.fromRecipes", { titles: from })}</span>}
              {item.isManual && <span>{t("shopping.manual")}</span>}
              {item.isWeekly && <span data-testid="shop-weekly-badge">{t("shopping.weekly")}</span>}
              {item.quantityChangedSinceChecked && <span className="shop-changed">{t("shopping.quantityChanged")}</span>}
            </span>
          )}
        </span>
      </label>
      {canHaveIt && (
        <button
          type="button"
          className="button shop-have"
          disabled={!canRemove}
          aria-label={t("shopping.haveItFor", { name: item.name })}
          onClick={() => void onHaveIt!(item.ingredientId!)}
        >
          {t("shopping.haveIt")}
        </button>
      )}
      {canWeHaveThis && (
        <button
          type="button"
          className="button shop-have"
          data-testid="we-have-this"
          disabled={!canRemove}
          aria-expanded={haveOpen}
          aria-label={t("shopping.weHaveThisFor", { name: item.name })}
          onClick={() => setHaveOpen((open) => !open)}
        >
          {t("shopping.weHaveThis")}
        </button>
      )}
      {(item.isManual || item.isWeekly) && (
        <button
          type="button"
          className="button shop-remove"
          disabled={!canRemove}
          aria-label={t(item.isManual ? "shopping.remove" : "shopping.removeWeekly", { name: item.name })}
          onClick={() => void onRemove(item.id)}
        >
          <span aria-hidden="true">&times;</span>
        </button>
      )}
      {haveOpen && canWeHaveThis && (
        <HaveForm
          name={item.name}
          onSave={async (quantity, unitId) => {
            await onWeHaveThis!(item.ingredientId!, quantity, unitId);
            setHaveOpen(false);
          }}
          onCancel={() => setHaveOpen(false)}
        />
      )}
    </li>
  );
}

/** "We have this" on a list item: an optional amount and unit, then the pantry is saved and the week regenerated. */
function HaveForm({
  name,
  onSave,
  onCancel,
}: {
  name: string;
  onSave: (quantity: number | null, unitId: string | null) => Promise<void>;
  onCancel: () => void;
}) {
  const { t } = useI18n();
  const amountId = useId();
  const unitId = useId();
  const [quantity, setQuantity] = useState("");
  const [unit, setUnit] = useState("");
  const [units, setUnits] = useState<Unit[]>([]);
  const [busy, setBusy] = useState(false);

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

  const amount = quantity.trim() === "" ? null : Number(quantity.trim().replace(",", "."));
  // The API wants an amount and a unit together, or neither.
  const invalid = (amount !== null && (!Number.isFinite(amount) || amount <= 0 || !unit)) || (amount === null && unit !== "");

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (invalid || busy) return;
    setBusy(true);
    try {
      await onSave(amount, amount === null ? null : unit);
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="shop-have-form" data-testid="have-form" onSubmit={(e) => void submit(e)} aria-label={t("shopping.weHaveThisFor", { name })}>
      <div className="shop-add-qty">
        <div className="field">
          <label htmlFor={amountId}>{t("shopping.haveAmount")}</label>
          <input id={amountId} inputMode="decimal" value={quantity} onChange={(e) => setQuantity(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor={unitId}>{t("shopping.haveUnit")}</label>
          <select id={unitId} value={unit} onChange={(e) => setUnit(e.target.value)}>
            <option value="">{t("shopping.haveNoUnit")}</option>
            {units.map((u) => (
              <option key={u.id} value={u.id}>
                {u.code}
              </option>
            ))}
          </select>
        </div>
      </div>
      <div className="shop-have-actions">
        <button type="submit" className="button" disabled={invalid || busy}>
          {t("shopping.haveSave")}
        </button>
        <button type="button" className="button button-outline" onClick={onCancel}>
          {t("shopping.haveCancel")}
        </button>
      </div>
    </form>
  );
}

/** "N staples you have at home", expandable, so a confirmed staple never silently disappears and can be undone. */
function PantrySummary({
  items,
  canChange,
  onRemove,
}: {
  items: PantryItem[];
  canChange: boolean;
  onRemove: (ingredientId: string) => Promise<void>;
}) {
  const { t } = useI18n();
  if (items.length === 0) return null;
  return (
    <details className="shop-pantry" data-testid="pantry">
      <summary>{t("shopping.pantry", { count: items.length })}</summary>
      <p className="shop-hint">{t("shopping.pantryHint")}</p>
      <ul className="shop-items">
        {items.map((p) => (
          <li key={p.ingredientId} className="shop-row">
            <span className="shop-text shop-pantry-name">
              <span className="shop-name">{p.name}</span>
            </span>
            <button
              type="button"
              className="button shop-have"
              disabled={!canChange}
              aria-label={t("shopping.pantryRemoveFor", { name: p.name })}
              onClick={() => void onRemove(p.ingredientId)}
            >
              {t("shopping.pantryRemove")}
            </button>
          </li>
        ))}
      </ul>
    </details>
  );
}

function AddItemForm({
  disabled,
  onAdd,
}: {
  disabled: boolean;
  onAdd: (label: string, quantity: number | null, unitId: string | null) => Promise<void>;
}) {
  const { t } = useI18n();
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
      <h2>{t("shopping.addTitle")}</h2>
      <div className="field">
        <label htmlFor="shop-add-label">{t("shopping.addLabel")}</label>
        <input id="shop-add-label" value={label} maxLength={100} onChange={(e) => setLabel(e.target.value)} />
      </div>
      <div className="shop-add-qty">
        <div className="field">
          <label htmlFor="shop-add-qty">{t("shopping.addQuantity")}</label>
          <input id="shop-add-qty" inputMode="decimal" value={quantity} onChange={(e) => setQuantity(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor="shop-add-unit">{t("shopping.addUnit")}</label>
          <select id="shop-add-unit" value={unitId} onChange={(e) => setUnitId(e.target.value)}>
            <option value="">{t("shopping.addNoUnit")}</option>
            {units.map((u) => (
              <option key={u.id} value={u.id}>
                {u.code}
              </option>
            ))}
          </select>
        </div>
      </div>
      <button type="submit" className="button" disabled={disabled || busy || invalid}>
        {t("shopping.add")}
      </button>
      {disabled && <p className="shop-hint">{t("shopping.needsOnline")}</p>}
    </form>
  );
}
