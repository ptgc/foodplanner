import { useEffect, useId, useMemo, useRef, useState, type FormEvent } from "react";
import { Link } from "react-router";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { call, failureKey } from "../household/call";
import { ConfirmDialog } from "../household/confirm-dialog";
import { powers } from "../household/roles";
import { useLoaded } from "../household/use-loaded";
import { useI18n, type MessageKey, type Params } from "../i18n";
import { searchDelayMs } from "../recipes/ingredient-picker";
import { searchIngredients, type Ingredient } from "../recipes/use-catalog";
import { useSession } from "../session";
import { STORE_SECTIONS, storeSectionKey, type StoreSection } from "../shopping/store-sections";

type Own = components["schemas"]["OwnIngredientResponse"];
type Usage = components["schemas"]["IngredientUsageResponse"];

type Notice = { key: MessageKey; params?: Params };
type Rowed = { id: string; key: MessageKey; params?: Params };

const usageParams = (u: Usage): Params => ({ recipes: u.recipes, lists: u.shoppingLists, pantry: u.pantryItems, drafts: u.importDrafts });
const isUsed = (u: Usage) => Number(u.recipes) + Number(u.shoppingLists) + Number(u.pantryItems) + Number(u.importDrafts) > 0;

/** The usage counts the API sends with `409 ingredient-in-use` (a ProblemDetails extension member). */
function usageOf(body: unknown): Usage | null {
  const usage = (body as { usage?: Partial<Usage> } | null)?.usage;
  if (!usage) return null;
  return { recipes: usage.recipes ?? 0, shoppingLists: usage.shoppingLists ?? 0, pantryItems: usage.pantryItems ?? 0, importDrafts: usage.importDrafts ?? 0 };
}

/**
 * Manage the household's own ingredients (T-079): usage counts, rename, delete and merge into another ingredient.
 * Adults and owners only (the API enforces it; a child gets a note instead of the screen).
 */
export default function IngredientsPage() {
  const { t } = useI18n();
  const { session } = useSession();
  if (session.status !== "signedIn") return null;
  const active = session.households.find((h) => h.id === session.activeHouseholdId);
  if (!active) return null;
  return (
    <section className="household ingredients" aria-labelledby="ingredients-title">
      <h1 id="ingredients-title">{t("ingredients.title")}</h1>
      <p>
        <Link to="/household">{t("ingredients.back")}</Link>
      </p>
      {powers(active.role).ingredients ? <Manage householdId={active.id} /> : <p className="household-note">{t("ingredients.adultsOnly")}</p>}
    </section>
  );
}

function Manage({ householdId }: { householdId: string }) {
  const { t } = useI18n();
  const searchId = useId();
  const [filter, setFilter] = useState("");
  const [notice, setNotice] = useState<Notice | null>(null);
  const [rowError, setRowError] = useState<Rowed | null>(null);
  const [renaming, setRenaming] = useState<string | null>(null);
  const [merging, setMerging] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<Own | null>(null);
  const [pendingMerge, setPendingMerge] = useState<{ source: Own; target: Ingredient } | null>(null);
  const [dialogError, setDialogError] = useState<MessageKey | null>(null);
  const [busy, setBusy] = useState(false);
  const [warnings, setWarnings] = useState<Notice[]>([]);

  const { data: items, error: loadError, reload } = useLoaded<Own[]>(householdId, () =>
    call(() => api.GET("/api/households/{householdId}/ingredients/own", { params: { path: { householdId } } })),
  );

  const shown = useMemo(() => {
    const term = filter.trim().toLocaleLowerCase();
    return (items ?? []).filter((i) => term === "" || i.name.toLocaleLowerCase().includes(term));
  }, [items, filter]);

  const rename = async (item: Own, name: string): Promise<MessageKey | null> => {
    const result = await call(() =>
      api.PUT("/api/households/{householdId}/ingredients/{ingredientId}", {
        params: { path: { householdId, ingredientId: item.id } },
        body: { name },
      }),
    );
    if (!result.ok) return result.key;
    setRenaming(null);
    setNotice({ key: "ingredients.renamed", params: { name: result.data?.name ?? name } });
    reload();
    return null;
  };

  const changeSection = async (item: Own, section: StoreSection) => {
    setRowError(null);
    const result = await call(() =>
      api.PUT("/api/households/{householdId}/ingredients/{ingredientId}", {
        params: { path: { householdId, ingredientId: item.id } },
        body: { name: item.name, section },
      }),
    );
    if (!result.ok) {
      setRowError({ id: item.id, key: result.key });
      return;
    }
    setNotice({ key: "ingredients.sectionSaved", params: { name: item.name, section: t(storeSectionKey(section)) } });
    reload();
  };

  const confirmDelete = async () => {
    if (!deleting || busy) return;
    setBusy(true);
    setDialogError(null);
    const target = deleting;
    let result: Awaited<ReturnType<typeof api.DELETE>> | null = null;
    try {
      result = await api.DELETE("/api/households/{householdId}/ingredients/{ingredientId}", {
        params: { path: { householdId, ingredientId: target.id } },
      });
    } catch {
      setBusy(false);
      setDialogError("problem.unreachable");
      return;
    }
    setBusy(false);
    if (result.response.ok) {
      setDeleting(null);
      setNotice({ key: "ingredients.deleted", params: { name: target.name } });
      reload();
      return;
    }
    const usage = result.response.status === 409 ? usageOf(result.error) : null;
    setDeleting(null);
    if (usage) {
      setRowError({ id: target.id, key: "ingredients.inUse", params: { name: target.name, ...usageParams(usage) } });
      reload();
    } else {
      setRowError({ id: target.id, key: failureKey(result.response.status, result.error) });
    }
  };

  const confirmMerge = async () => {
    if (!pendingMerge || busy) return;
    const { source, target } = pendingMerge;
    setBusy(true);
    setDialogError(null);
    const result = await call(() =>
      api.POST("/api/households/{householdId}/ingredients/{ingredientId}/merge", {
        params: { path: { householdId, ingredientId: source.id } },
        body: { targetIngredientId: target.id },
      }),
    );
    setBusy(false);
    if (!result.ok) {
      setDialogError(result.key);
      return;
    }
    setPendingMerge(null);
    setMerging(null);
    setRowError(null);
    setNotice({ key: "ingredients.merged", params: { name: source.name, target: target.name } });
    // Warnings are shown after the success notice, one per code.
    const warnings = (result.data?.warnings ?? []).filter((w): w is "dimension-differs" | "staple-differs" => w === "dimension-differs" || w === "staple-differs");
    setWarnings(warnings.map((w) => ({ key: `ingredients.warning.${w}` as MessageKey, params: { name: source.name, target: target.name } })));
    reload();
  };

  return (
    <>
      <p>{t("ingredients.intro")}</p>
      {notice && (
        <p role="status" className="household-notice" data-testid="ingredients-notice">
          {t(notice.key, notice.params)}
        </p>
      )}
      {warnings.map((w) => (
        <p key={w.key} role="status" className="household-notice">
          {t(w.key, w.params)}
        </p>
      ))}
      <div className="field">
        <label htmlFor={searchId}>{t("ingredients.search")}</label>
        <input id={searchId} type="search" value={filter} autoComplete="off" onChange={(e) => setFilter(e.target.value)} />
      </div>
      {loadError && (
        <p role="alert" className="form-error">
          {t("ingredients.loadFailed")}
        </p>
      )}
      {!items && !loadError && <p>{t("ingredients.loading")}</p>}
      {items && items.length === 0 && <p>{t("ingredients.empty")}</p>}
      {items && items.length > 0 && shown.length === 0 && <p>{t("ingredients.noMatch")}</p>}
      <ul className="household-list" aria-label={t("ingredients.title")}>
        {shown.map((item) => (
          <li key={item.id} className="household-row" data-testid="ingredient-row">
            {renaming === item.id ? (
              <RenameForm item={item} onSave={(name) => rename(item, name)} onCancel={() => setRenaming(null)} />
            ) : (
              <>
                <div className="household-row-main">
                  <span className="household-row-title">{item.name}</span>
                  <span className="household-row-sub">
                    {isUsed(item.usage) ? t("ingredients.usage", usageParams(item.usage)) : t("ingredients.unused")}
                  </span>
                </div>
                <SectionPicker item={item} onChange={(section) => changeSection(item, section)} />
                <div className="household-row-actions">
                  <button type="button" className="button button-outline" aria-label={t("ingredients.renameFor", { name: item.name })} onClick={() => { setRenaming(item.id); setMerging(null); setRowError(null); }}>
                    {t("ingredients.rename")}
                  </button>
                  <button type="button" className="button button-outline" aria-label={t("ingredients.mergeFor", { name: item.name })} onClick={() => { setMerging(merging === item.id ? null : item.id); setRenaming(null); setRowError(null); }}>
                    {t("ingredients.merge")}
                  </button>
                  <button type="button" className="button button-outline" aria-label={t("ingredients.deleteFor", { name: item.name })} onClick={() => { setDeleting(item); setDialogError(null); setRowError(null); }}>
                    {t("ingredients.delete")}
                  </button>
                </div>
              </>
            )}
            {rowError?.id === item.id && (
              <p role="alert" className="household-row-error form-error">
                {t(rowError.key, rowError.params)}
              </p>
            )}
            {merging === item.id && (
              <MergePicker householdId={householdId} source={item} onPick={(target) => { setPendingMerge({ source: item, target }); setDialogError(null); }} />
            )}
          </li>
        ))}
      </ul>
      {deleting && (
        <ConfirmDialog
          title={t("ingredients.deleteTitle", { name: deleting.name })}
          body={t("ingredients.deleteBody")}
          confirmLabel={t("ingredients.deleteConfirm")}
          busy={busy}
          error={dialogError}
          onConfirm={() => void confirmDelete()}
          onCancel={() => setDeleting(null)}
        />
      )}
      {pendingMerge && (
        <ConfirmDialog
          title={t("ingredients.mergeTitle", { name: pendingMerge.source.name, target: pendingMerge.target.name })}
          body={t("ingredients.mergeBody", { name: pendingMerge.source.name, target: pendingMerge.target.name, ...usageParams(pendingMerge.source.usage) })}
          confirmLabel={t("ingredients.mergeConfirm")}
          busy={busy}
          error={dialogError}
          onConfirm={() => void confirmMerge()}
          onCancel={() => setPendingMerge(null)}
        />
      )}
    </>
  );
}

/** The store section the shopping list groups this ingredient under (T-085). Own ingredients start in "Other". */
function SectionPicker({ item, onChange }: { item: Own; onChange: (section: StoreSection) => void }) {
  const { t } = useI18n();
  const selectId = useId();
  return (
    <div className="ingredient-section">
      <label htmlFor={selectId}>{t("ingredients.sectionFor", { name: item.name })}</label>
      <select id={selectId} value={item.section} onChange={(e) => onChange(e.target.value as StoreSection)}>
        {STORE_SECTIONS.map((section) => (
          <option key={section} value={section}>
            {t(storeSectionKey(section))}
          </option>
        ))}
      </select>
    </div>
  );
}

function RenameForm({ item, onSave, onCancel }: { item: Own; onSave: (name: string) => Promise<MessageKey | null>; onCancel: () => void }) {
  const { t } = useI18n();
  const inputId = useId();
  const [name, setName] = useState(item.name);
  const [error, setError] = useState<MessageKey | null>(null);
  const [busy, setBusy] = useState(false);
  const field = useRef<HTMLInputElement>(null);
  useEffect(() => field.current?.focus(), []);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (busy || name.trim() === "") return;
    setBusy(true);
    setError(null);
    const failure = await onSave(name.trim());
    setBusy(false);
    if (failure) setError(failure);
  };

  return (
    <form className="household-inline" onSubmit={(e) => void submit(e)}>
      <div className="field">
        <label htmlFor={inputId}>{t("ingredients.renameLabel", { name: item.name })}</label>
        <input id={inputId} value={name} maxLength={100} ref={field} onChange={(e) => setName(e.target.value)} />
      </div>
      <button type="submit" className="button" disabled={busy || name.trim() === ""}>
        {t("ingredients.save")}
      </button>
      <button type="button" className="button button-outline" disabled={busy} onClick={onCancel}>
        {t("ingredients.cancel")}
      </button>
      {error && (
        <p role="alert" className="household-row-error form-error">
          {t(error)}
        </p>
      )}
    </form>
  );
}

/** Searches the catalog (global and own, like the recipe ingredient picker) for the ingredient to merge into. */
function MergePicker({ householdId, source, onPick }: { householdId: string; source: Own; onPick: (target: Ingredient) => void }) {
  const { t, locale } = useI18n();
  const inputId = useId();
  const [term, setTerm] = useState("");
  const [found, setFound] = useState<{ term: string; items: Ingredient[] } | null>(null);
  const [failed, setFailed] = useState(false);
  const trimmed = term.trim();

  useEffect(() => {
    if (trimmed === "") return;
    let cancelled = false;
    const timer = setTimeout(() => {
      void searchIngredients(householdId, trimmed).then((result) => {
        if (cancelled) return;
        if (result.ok) {
          setFound({ term: trimmed, items: result.value });
          setFailed(false);
        } else setFailed(true);
      });
    }, searchDelayMs);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [householdId, trimmed, locale]);

  const current = found && found.term === trimmed ? found.items.filter((i) => i.id !== source.id) : null;
  return (
    <div className="ingredient-picker household-row-error">
      <label htmlFor={inputId}>{t("ingredients.mergeSearch", { name: source.name })}</label>
      <input id={inputId} type="search" value={term} autoComplete="off" enterKeyHint="search" onChange={(e) => setTerm(e.target.value)} />
      {failed && <p role="alert">{t("problem.generic")}</p>}
      {current && current.length > 0 && (
        <ul className="ingredient-results" aria-label={t("ingredients.mergeResults")}>
          {current.map((item) => (
            <li key={item.id}>
              <button type="button" className="ingredient-result" aria-label={t("ingredients.mergePick", { name: item.name })} onClick={() => onPick(item)}>
                <span>{item.name}</span>
                {!item.isGlobal && <span className="tag-pill">{t("ingredients.mergeOwn")}</span>}
              </button>
            </li>
          ))}
        </ul>
      )}
      {current && current.length === 0 && <p className="ingredient-none">{t("ingredients.mergeNone")}</p>}
    </div>
  );
}
