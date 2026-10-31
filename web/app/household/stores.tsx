import { useId, useState, type FormEvent } from "react";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { useI18n, type MessageKey } from "../i18n";
import { STORE_SECTIONS, storeSectionKey, type StoreSection } from "../shopping/store-sections";
import { call } from "./call";
import { ConfirmDialog } from "./confirm-dialog";
import { useLoaded } from "./use-loaded";

type Store = components["schemas"]["StoreResponse"];

/** The most the API accepts for one household (Store.MaxPerHousehold). */
export const maxStores = 20;

/** `order` with the section at `index` moved one step (-1 up, +1 down); the same array when it cannot move. */
export function moveSection(order: StoreSection[], index: number, step: -1 | 1): StoreSection[] {
  const target = index + step;
  if (index < 0 || index >= order.length || target < 0 || target >= order.length) return order;
  const next = [...order];
  [next[index], next[target]] = [next[target]!, next[index]!];
  return next;
}

/**
 * The household's stores (T-087): each has the order in which that store's sections are walked. Everyone sees them; Owner and
 * Adult change them (the API enforces the same with HouseholdAdult). The choice of store is made on the shopping screen.
 */
export function Stores({ householdId, canEdit }: { householdId: string; canEdit: boolean }) {
  const { t } = useI18n();
  const [error, setError] = useState<MessageKey | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<Store | null>(null);
  const [deleteBusy, setDeleteBusy] = useState(false);
  const [deleteError, setDeleteError] = useState<MessageKey | null>(null);
  const { data: stores, error: loadError, reload } = useLoaded<Store[]>(householdId, () =>
    call(() => api.GET("/api/households/{householdId}/stores", { params: { path: { householdId } } })),
  );

  const confirmDelete = async () => {
    if (!deleting) return;
    setDeleteBusy(true);
    setDeleteError(null);
    const result = await call(() =>
      api.DELETE("/api/households/{householdId}/stores/{id}", { params: { path: { householdId, id: deleting.id } } }),
    );
    setDeleteBusy(false);
    if (result.ok) {
      setDeleting(null);
      reload();
    } else setDeleteError(result.key);
  };

  return (
    <div className="household-card" data-testid="stores">
      <h2>{t("household.stores.title")}</h2>
      <p>{t("household.stores.body")}</p>
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
      {!stores && !loadError && <p role="status">{t("household.stores.loading")}</p>}
      {stores && stores.length === 0 && <p data-testid="stores-empty">{t("household.stores.empty")}</p>}
      {stores && stores.length > 0 && (
        <ul className="household-list" data-testid="stores-list">
          {stores.map((store) => (
            <li key={store.id} className="household-row" data-testid="store-row">
              {editing === store.id ? (
                <StoreForm
                  householdId={householdId}
                  store={store}
                  onDone={() => {
                    setEditing(null);
                    reload();
                  }}
                  onCancel={() => setEditing(null)}
                />
              ) : (
                <>
                  <div className="household-row-main">
                    <span className="household-row-title">{store.name}</span>
                    <span className="household-row-sub">
                      {store.sectionOrder.map((s) => t(storeSectionKey(s))).join(" › ")}
                    </span>
                  </div>
                  {canEdit && (
                    <div className="household-row-actions">
                      <button
                        type="button"
                        className="button button-outline"
                        aria-label={t("household.stores.editFor", { name: store.name })}
                        onClick={() => {
                          setError(null);
                          setEditing(store.id);
                        }}
                      >
                        {t("household.stores.edit")}
                      </button>
                      <button
                        type="button"
                        className="button button-outline"
                        aria-label={t("household.stores.deleteFor", { name: store.name })}
                        onClick={() => {
                          setDeleteError(null);
                          setDeleting(store);
                        }}
                      >
                        {t("household.stores.delete")}
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
        stores && stores.length >= maxStores ? (
          <p className="household-note">{t("household.stores.full", { max: maxStores })}</p>
        ) : (
          <StoreForm householdId={householdId} store={null} onDone={reload} onCancel={null} />
        )
      ) : (
        <p className="household-note">{t("household.stores.adultsOnly")}</p>
      )}
      {deleting && (
        <ConfirmDialog
          title={t("household.stores.deleteTitle", { name: deleting.name })}
          body={t("household.stores.deleteBody")}
          confirmLabel={t("household.stores.delete")}
          busy={deleteBusy}
          error={deleteError}
          onConfirm={() => void confirmDelete()}
          onCancel={() => setDeleting(null)}
        />
      )}
    </div>
  );
}

/** Create (store null) or change a store: the name and the order of the sections, moved with up and down buttons. */
function StoreForm({
  householdId,
  store,
  onDone,
  onCancel,
}: {
  householdId: string;
  store: Store | null;
  onDone: () => void;
  onCancel: (() => void) | null;
}) {
  const { t } = useI18n();
  const nameId = useId();
  const [name, setName] = useState(store?.name ?? "");
  const [order, setOrder] = useState<StoreSection[]>(store ? [...store.sectionOrder] : [...STORE_SECTIONS]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const invalid = name.trim() === "" || name.trim().length > 100;

  const move = (index: number, step: -1 | 1) => setOrder((o) => moveSection(o, index, step));

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (invalid || busy) return;
    setBusy(true);
    setError(null);
    const body = { name: name.trim(), sectionOrder: order };
    const result = await call(() =>
      store
        ? api.PUT("/api/households/{householdId}/stores/{id}", { params: { path: { householdId, id: store.id } }, body })
        : api.POST("/api/households/{householdId}/stores", { params: { path: { householdId } }, body }),
    );
    setBusy(false);
    if (result.ok) {
      if (!store) {
        setName("");
        setOrder([...STORE_SECTIONS]);
      }
      onDone();
    } else setError(result.key);
  };

  return (
    <form className="household-store-form" onSubmit={(e) => void submit(e)} data-testid={store ? "store-edit" : "store-add"}>
      <h3>{store ? t("household.stores.editTitle") : t("household.stores.addTitle")}</h3>
      <div className="field">
        <label htmlFor={nameId}>{t("household.stores.name")}</label>
        <input id={nameId} value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
      </div>
      <p className="household-note" id={`${nameId}-order`}>
        {t("household.stores.orderHelp")}
      </p>
      <ol className="store-order" aria-describedby={`${nameId}-order`} data-testid="store-order">
        {order.map((section, index) => {
          const label = t(storeSectionKey(section));
          return (
            <li key={section} className="store-order-row" data-testid="store-order-row">
              <span className="store-order-name">
                {index + 1}. {label}
              </span>
              <button
                type="button"
                className="button button-outline store-order-button"
                aria-label={t("household.stores.moveUp", { name: label })}
                disabled={index === 0}
                onClick={() => move(index, -1)}
              >
                <span aria-hidden="true">&uarr;</span>
              </button>
              <button
                type="button"
                className="button button-outline store-order-button"
                aria-label={t("household.stores.moveDown", { name: label })}
                disabled={index === order.length - 1}
                onClick={() => move(index, 1)}
              >
                <span aria-hidden="true">&darr;</span>
              </button>
            </li>
          );
        })}
      </ol>
      {error && (
        <p role="alert" className="form-error">
          {t(error)}
        </p>
      )}
      <div className="household-actions">
        <button type="submit" className="button" disabled={invalid || busy}>
          {store ? t("household.stores.save") : t("household.stores.add")}
        </button>
        {onCancel && (
          <button type="button" className="button button-outline" onClick={onCancel}>
            {t("household.stores.cancel")}
          </button>
        )}
      </div>
    </form>
  );
}
