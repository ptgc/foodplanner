import { useEffect, useId, useMemo, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { useI18n, type I18n } from "../i18n";
import { problemKey } from "../i18n/problems";
import {
  emptyFilters,
  hasFilters,
  maxMinuteOptions,
  parseFilters,
  toSearchParams,
  toggleMaxMinutes,
  toggleTag,
  type RecipeFilters,
} from "../recipes/filters";
import { RecipeImage } from "../recipes/recipe-image";
import { useRecipeList, useTags, type Failure, type RecipeSummary, type Tag } from "../recipes/use-recipes";
import { useSession } from "../session";

/** How long typing must pause before the search is applied (and written to the URL). */
export const searchDebounceMs = 300;
const skeletonCount = 6;
const tagsOnCard = 3;

export function failureText(t: I18n["t"], failure: Failure): string {
  return failure === "unreachable" ? t("problem.unreachable") : t(problemKey(failure.problemType));
}

export default function Recipes() {
  const { t } = useI18n();
  const { session } = useSession();
  if (session.status !== "signedIn") return null;
  if (!session.activeHouseholdId) return <p>{t("recipes.noHousehold")}</p>;
  return <RecipesScreen key={session.activeHouseholdId} householdId={session.activeHouseholdId} />;
}

function RecipesScreen({ householdId }: { householdId: string }) {
  const { t } = useI18n();
  const [params, setParams] = useSearchParams();
  const filters = useMemo(() => parseFilters(params), [params]);
  const tags = useTags(householdId);
  const { list, loadMore, retry } = useRecipeList(householdId, filters);
  const searchId = useId();

  // The box shows what is typed; the URL (and the request) follow after a pause. A change that did not come from
  // typing here (back/forward, "clear filters") is copied into the box.
  const [text, setText] = useState(filters.search);
  const applied = useRef(filters.search);
  useEffect(() => {
    if (filters.search !== applied.current) {
      applied.current = filters.search;
      setText(filters.search);
    }
  }, [filters.search]);
  useEffect(() => {
    if (text === applied.current) return;
    const timer = setTimeout(() => {
      applied.current = text;
      setParams((prev) => toSearchParams({ ...parseFilters(prev), search: text }), { replace: true });
    }, searchDebounceMs);
    return () => clearTimeout(timer);
  }, [text, setParams]);

  // Chips and "clear" make a history entry, so the back button undoes them; typing replaces the entry.
  const apply = (next: RecipeFilters) => setParams(toSearchParams(next));
  const active = hasFilters(filters) || text.trim() !== "";
  const categories = tags.filter((tag) => tag.kind === "Category");
  const plainTags = tags.filter((tag) => tag.kind === "Tag");

  return (
    <section className="recipes" aria-labelledby="recipes-heading">
      <h1 id="recipes-heading">{t("recipes.title")}</h1>

      <div className="recipes-search">
        <label htmlFor={searchId}>{t("recipes.search")}</label>
        <input
          id={searchId}
          type="search"
          value={text}
          autoComplete="off"
          enterKeyHint="search"
          onChange={(e) => setText(e.target.value)}
        />
      </div>

      <ChipGroup label={t("recipes.maxTime")}>
        {maxMinuteOptions.map((minutes) => (
          <Chip
            key={minutes}
            pressed={filters.maxMinutes === minutes}
            onClick={() => apply(toggleMaxMinutes(filters, minutes))}
          >
            {t("recipes.maxMinutes", { minutes })}
          </Chip>
        ))}
      </ChipGroup>
      {categories.length > 0 && (
        <ChipGroup label={t("recipes.categories")}>
          {categories.map((tag) => (
            <TagChip key={tag.id} tag={tag} filters={filters} apply={apply} />
          ))}
        </ChipGroup>
      )}
      {plainTags.length > 0 && (
        <ChipGroup label={t("recipes.tags")}>
          {plainTags.map((tag) => (
            <TagChip key={tag.id} tag={tag} filters={filters} apply={apply} />
          ))}
        </ChipGroup>
      )}

      <div className="recipes-status">
        <p role="status" aria-live="polite">
          {list.status === "ok" ? t("recipes.count", { count: list.total }) : ""}
        </p>
        {active && (
          <button type="button" className="button button-outline" onClick={() => apply(emptyFilters)}>
            {t("recipes.clearFilters")}
          </button>
        )}
      </div>

      {list.status === "loading" && <Skeleton />}
      {list.status === "error" && (
        <div className="recipes-message" role="alert">
          <p>{t("recipes.loadFailed")}</p>
          <p>{failureText(t, list.failure)}</p>
          <button type="button" className="button" onClick={retry}>
            {t("recipes.retry")}
          </button>
        </div>
      )}
      {list.status === "ok" && list.items.length === 0 && (
        <div className="recipes-message" data-testid="recipes-empty">
          <p className="recipes-message-title">{active ? t("recipes.noMatch") : t("recipes.empty")}</p>
          <p>{active ? t("recipes.noMatchHint") : t("recipes.emptyHint")}</p>
        </div>
      )}
      {list.status === "ok" && list.items.length > 0 && (
        <>
          <ul className="recipe-grid">
            {list.items.map((recipe) => (
              <li key={recipe.id}>
                <RecipeCard recipe={recipe} />
              </li>
            ))}
          </ul>
          {list.moreFailure && (
            <p className="recipes-more-error" role="alert">
              {t("recipes.loadFailed")} {failureText(t, list.moreFailure)}
            </p>
          )}
          {list.hasMore && (
            <div className="recipes-more">
              <button type="button" className="button" onClick={() => void loadMore()} disabled={list.loadingMore}>
                {list.loadingMore ? t("recipes.loadingMore") : list.moreFailure ? t("recipes.retry") : t("recipes.loadMore")}
              </button>
            </div>
          )}
        </>
      )}
    </section>
  );
}

function ChipGroup({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="chip-group" role="group" aria-label={label}>
      {children}
    </div>
  );
}

function Chip({ pressed, onClick, children }: { pressed: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button type="button" className="chip" aria-pressed={pressed} onClick={onClick}>
      {children}
    </button>
  );
}

function TagChip({ tag, filters, apply }: { tag: Tag; filters: RecipeFilters; apply: (f: RecipeFilters) => void }) {
  // Names of global tags come from the catalog (translation: T-039); a household's own are shown as typed.
  return (
    <Chip pressed={filters.tagIds.includes(tag.id)} onClick={() => apply(toggleTag(filters, tag.id))}>
      {tag.name}
    </Chip>
  );
}

function RecipeCard({ recipe }: { recipe: RecipeSummary }) {
  const { t } = useI18n();
  const minutes = recipe.totalMinutes === null || recipe.totalMinutes === undefined ? null : Number(recipe.totalMinutes);
  return (
    <Link to={`/recipes/${recipe.id}`} className="recipe-card" data-testid="recipe-card">
      <RecipeImage title={recipe.title} />
      <span className="recipe-card-body">
        <span className="recipe-card-title">{recipe.title}</span>
        {minutes !== null && <span className="recipe-card-time">{t("recipes.minutes", { minutes })}</span>}
        {recipe.tags.length > 0 && (
          <span className="recipe-card-tags">
            {recipe.tags.slice(0, tagsOnCard).map((tag) => (
              <span key={tag.id} className="tag-pill">
                {tag.name}
              </span>
            ))}
          </span>
        )}
      </span>
    </Link>
  );
}

function Skeleton() {
  const { t } = useI18n();
  return (
    <div aria-busy="true">
      <p role="status" className="visually-hidden">
        {t("recipes.loadingList")}
      </p>
      <ul className="recipe-grid" aria-hidden="true" data-testid="recipes-skeleton">
        {Array.from({ length: skeletonCount }, (_, i) => (
          <li key={i} className="recipe-skeleton">
            <div className="recipe-skeleton-image" />
            <div className="recipe-skeleton-line" />
            <div className="recipe-skeleton-line short" />
          </li>
        ))}
      </ul>
    </div>
  );
}
