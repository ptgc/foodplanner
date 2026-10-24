import { useEffect, useId, useRef, useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router";
import { api } from "../api/client";
import { useI18n, type I18n, type MessageKey } from "../i18n";
import { problemKey, problemTypeOf } from "../i18n/problems";
import { avatarColor, initialsOf } from "../planner/avatar";
import {
  maxServings,
  minServings,
  usePlan,
  type Failure,
  type Images,
  type PlanEntry,
  type WriteResult,
} from "../planner/use-plan";
import { emptyFilters, toggleTag, type RecipeFilters } from "../recipes/filters";
import { RecipeImage } from "../recipes/recipe-image";
import { useRecipeList, useTags, type RecipeSummary } from "../recipes/use-recipes";
import { useSession } from "../session";
import { addDays, addWeeks, dateOf, isMonday, isoWeek, mondayOf, toIsoDate, weekRange } from "../shopping/week";

/** How long typing must pause before the picker searches. */
export const pickerSearchDebounceMs = 300;

const failureText = (t: I18n["t"], failure: Failure) =>
  failure === "unreachable" ? t("problem.unreachable") : t(problemKey(failure.problemType));

export default function PlanRoute() {
  const { t } = useI18n();
  const { session } = useSession();
  if (session.status !== "signedIn") return null;
  if (!session.activeHouseholdId) return <p>{t("planner.noHousehold")}</p>;
  const role = session.households.find((h) => h.id === session.activeHouseholdId)?.role;
  return (
    <PlanScreen
      key={session.activeHouseholdId}
      householdId={session.activeHouseholdId}
      userId={session.user.userId}
      isChild={role === "Child"}
    />
  );
}

function PlanScreen({ householdId, userId, isChild }: { householdId: string; userId: string; isChild: boolean }) {
  const i18n = useI18n();
  const { t, locale, formatDayMonth, formatNumber } = i18n;
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const requested = params.get("week");
  const weekStart = isMonday(requested) ? requested : mondayOf(new Date());
  const { week, year } = isoWeek(weekStart);
  const { plan, images, add, setServings, remove, reload } = usePlan(householdId, weekStart);

  const [picking, setPicking] = useState<string | null>(null);
  const opener = useRef<HTMLElement | null>(null);
  const [busy, setBusy] = useState<ReadonlySet<string>>(new Set());
  const [notice, setNotice] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [generating, setGenerating] = useState(false);

  const today = toIsoDate(new Date());
  const days = Array.from({ length: 7 }, (_, i) => addDays(weekStart, i));
  const dayName = (date: string) => new Intl.DateTimeFormat(locale, { weekday: "long" }).format(dateOf(date));
  const dayLabel = (date: string) => `${dayName(date)} ${formatDayMonth(dateOf(date))}`;

  const goToWeek = (monday: string) => {
    setNotice(null);
    setProblem(null);
    setParams({ week: monday }, { replace: true });
  };

  const report = (r: WriteResult, success?: string) => {
    if (r.ok) {
      setProblem(null);
      setNotice(success ?? null);
    } else {
      setNotice(null);
      setProblem(t(r.message));
    }
  };

  const withBusy = async (id: string, run: () => Promise<WriteResult>) => {
    setBusy((prev) => new Set(prev).add(id));
    try {
      return await run();
    } finally {
      setBusy((prev) => {
        const next = new Set(prev);
        next.delete(id);
        return next;
      });
    }
  };

  const pick = async (date: string, recipe: RecipeSummary): Promise<WriteResult> => {
    const result = await add(date, recipe);
    if (result.ok) {
      report(result, t("planner.added", { title: recipe.title, day: dayLabel(date) }));
      closePicker();
    }
    return result;
  };

  const closePicker = () => {
    setPicking(null);
    // Return focus to the "+" that opened the sheet.
    queueMicrotask(() => opener.current?.focus());
  };

  const generate = async () => {
    setGenerating(true);
    setProblem(null);
    try {
      const { response, error } = await api.POST("/api/households/{householdId}/shopping-lists/{weekStart}/generate", {
        params: { path: { householdId, weekStart } },
      });
      if (response.ok) {
        void navigate(`/shopping?week=${weekStart}`);
        return;
      }
      const type = problemTypeOf(error);
      setProblem(type ? t(problemKey(type)) : t("planner.generateFailed"));
    } catch {
      setProblem(t("problem.unreachable"));
    } finally {
      setGenerating(false);
    }
  };

  const entries = plan.status === "ok" ? plan.entries : [];
  const thisWeek = mondayOf(new Date());

  return (
    <section className="planner" aria-labelledby="planner-title">
      <div inert={picking !== null}>
        <h1 id="planner-title">{t("planner.title")}</h1>

        <div className="planner-weeks">
          <button type="button" className="button" onClick={() => goToWeek(addWeeks(weekStart, -1))}>
            <span aria-hidden="true">&lsaquo;</span>
            <span className="visually-hidden">{t("planner.previousWeek")}</span>
          </button>
          <p className="planner-week" aria-live="polite" data-testid="planner-week">
            <strong>
              {t("planner.week", {
                week: formatNumber(week, { useGrouping: false }),
                year: formatNumber(year, { useGrouping: false }),
              })}
            </strong>
            <span>{weekRange(weekStart, formatDayMonth)}</span>
          </p>
          <button type="button" className="button" onClick={() => goToWeek(addWeeks(weekStart, 1))}>
            <span aria-hidden="true">&rsaquo;</span>
            <span className="visually-hidden">{t("planner.nextWeek")}</span>
          </button>
        </div>
        {weekStart !== thisWeek && (
          <button type="button" className="button button-outline planner-today" onClick={() => goToWeek(thisWeek)}>
            {t("planner.thisWeek")}
          </button>
        )}

        <p role="status" className="planner-notice" data-testid="planner-notice">
          {notice}
        </p>
        {problem && (
          <p role="alert" className="form-error" data-testid="planner-problem">
            {problem}
          </p>
        )}

        {plan.status === "loading" && <p role="status">{t("planner.loading")}</p>}
        {plan.status === "error" && (
          <div role="alert" className="planner-message">
            <p>{t("planner.loadFailed")}</p>
            <p>{failureText(t, plan.failure)}</p>
            <button type="button" className="button" onClick={reload}>
              {t("planner.retry")}
            </button>
          </div>
        )}

        {plan.status === "ok" && (
          <>
            <ol className="planner-days">
              {days.map((date) => {
                const dayEntries = entries.filter((e) => e.date === date);
                return (
                  <li
                    key={date}
                    className="planner-day"
                    data-testid="planner-day"
                    data-date={date}
                    aria-current={date === today ? "date" : undefined}
                  >
                    <div className="planner-day-head">
                      <h2>
                        <span className="planner-day-name">{dayName(date)}</span>
                        <span className="planner-day-date">
                          {formatDayMonth(dateOf(date))}
                          {date === today && <> ({t("planner.today")})</>}
                        </span>
                      </h2>
                      <button
                        type="button"
                        className="planner-add"
                        aria-label={t("planner.addMeal", { day: dayLabel(date) })}
                        onClick={(e) => {
                          opener.current = e.currentTarget;
                          setNotice(null);
                          setPicking(date);
                        }}
                      >
                        <span aria-hidden="true">+</span>
                      </button>
                    </div>
                    {dayEntries.length === 0 ? (
                      <p className="planner-empty">{t("planner.nothingPlanned")}</p>
                    ) : (
                      <ul className="planner-entries">
                        {dayEntries.map((entry) => (
                          <li key={entry.id}>
                            <EntryCard
                              entry={entry}
                              images={images}
                              dayLabel={dayLabel(date)}
                              canChange={!isChild || entry.pickedBy.userId === userId}
                              busy={busy.has(entry.id)}
                              onServings={(n) => withBusy(entry.id, () => setServings(entry, n)).then((r) => report(r))}
                              onRemove={() =>
                                withBusy(entry.id, () => remove(entry)).then((r) =>
                                  report(r, t("planner.removed", { title: entry.recipeTitle })),
                                )
                              }
                            />
                          </li>
                        ))}
                      </ul>
                    )}
                  </li>
                );
              })}
            </ol>

            <div className="planner-actions">
              <Link className="button button-outline" to={`/shopping?week=${weekStart}`}>
                {t("planner.shoppingList")}
              </Link>
              <button
                type="button"
                className="button"
                disabled={generating || entries.length === 0}
                onClick={() => void generate()}
              >
                {generating ? t("planner.generating") : t("planner.generate")}
              </button>
            </div>
          </>
        )}
      </div>

      {picking !== null && (
        <Picker
          key={picking}
          householdId={householdId}
          title={t("planner.pickerTitle", { day: dayLabel(picking) })}
          onClose={closePicker}
          onPick={(recipe) => pick(picking, recipe)}
        />
      )}
    </section>
  );
}

function EntryCard({
  entry,
  images,
  dayLabel,
  canChange,
  busy,
  onServings,
  onRemove,
}: {
  entry: PlanEntry;
  images: Images;
  dayLabel: string;
  canChange: boolean;
  busy: boolean;
  onServings: (servings: number) => void;
  onRemove: () => void;
}) {
  const { t, formatNumber } = useI18n();
  const servings = Number(entry.servings);
  const image = images[entry.recipeId];
  const who = entry.pickedBy.email ?? t("planner.unknownPicker");
  return (
    <article className="planner-entry" data-testid="planner-entry" aria-label={entry.recipeTitle}>
      <div className="planner-entry-image">
        <RecipeImage
          title={entry.recipeTitle}
          thumbUrl={image?.thumbUrl}
          cardUrl={image?.cardUrl}
          sizes="112px"
        />
      </div>
      <div className="planner-entry-body">
        <p className="planner-entry-title">{entry.recipeTitle}</p>
        <div className="planner-entry-controls">
          {canChange ? (
            <div className="stepper" role="group" aria-label={t("planner.servings", { count: servings })}>
              <button
                type="button"
                className="stepper-button"
                disabled={busy || servings <= minServings}
                aria-label={t("planner.fewer", { title: entry.recipeTitle })}
                onClick={() => onServings(servings - 1)}
              >
                <span aria-hidden="true">&minus;</span>
              </button>
              <span className="stepper-value" data-testid="servings" aria-live="polite">
                {t("planner.servings", { count: formatNumber(servings) })}
              </span>
              <button
                type="button"
                className="stepper-button"
                disabled={busy || servings >= maxServings}
                aria-label={t("planner.more", { title: entry.recipeTitle })}
                onClick={() => onServings(servings + 1)}
              >
                <span aria-hidden="true">+</span>
              </button>
            </div>
          ) : (
            <span className="stepper-value" data-testid="servings">
              {t("planner.servings", { count: formatNumber(servings) })}
            </span>
          )}
          <span
            className="avatar"
            role="img"
            aria-label={t("planner.pickedBy", { who })}
            style={{ backgroundColor: avatarColor(entry.pickedBy.userId) }}
            data-testid="avatar"
          >
            {initialsOf(entry.pickedBy.email, entry.pickedBy.userId)}
          </span>
          {canChange && (
            <button
              type="button"
              className="planner-remove"
              disabled={busy}
              aria-label={t("planner.remove", { title: entry.recipeTitle, day: dayLabel })}
              onClick={onRemove}
            >
              <span aria-hidden="true">&times;</span>
            </button>
          )}
        </div>
      </div>
    </article>
  );
}

/**
 * Bottom sheet with the recipe grid (search and category chips). One tap on a recipe adds it to the day and closes
 * the sheet, so a meal is two taps from the week view: the day's "+", then the recipe.
 */
function Picker({
  householdId,
  title,
  onClose,
  onPick,
}: {
  householdId: string;
  title: string;
  onClose: () => void;
  onPick: (recipe: RecipeSummary) => Promise<WriteResult>;
}) {
  const { t } = useI18n();
  const headingId = useId();
  const searchId = useId();
  const sheet = useRef<HTMLDivElement>(null);
  const [text, setText] = useState("");
  const [filters, setFilters] = useState<RecipeFilters>(emptyFilters);
  const [adding, setAdding] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const tags = useTags(householdId);
  const { list, loadMore, retry } = useRecipeList(householdId, filters);
  const categories = tags.filter((tag) => tag.kind === "Category");

  const closeRef = useRef(onClose);
  useEffect(() => {
    closeRef.current = onClose;
  });
  useEffect(() => {
    sheet.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") closeRef.current();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, []);

  useEffect(() => {
    const timer = setTimeout(() => setFilters((f) => (f.search === text ? f : { ...f, search: text })), pickerSearchDebounceMs);
    return () => clearTimeout(timer);
  }, [text]);

  const choose = async (recipe: RecipeSummary) => {
    if (adding) return;
    setAdding(true);
    setError(null);
    const result = await onPick(recipe);
    if (!result.ok) {
      setAdding(false);
      setError(result.message);
    }
  };

  return (
    <div className="sheet-backdrop">
      <div
        ref={sheet}
        className="sheet"
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        tabIndex={-1}
      >
        <div className="sheet-head">
          <h2 id={headingId}>{title}</h2>
          <button type="button" className="button button-outline" onClick={onClose}>
            {t("planner.pickerClose")}
          </button>
        </div>
        <div className="sheet-body">
          <label htmlFor={searchId} className="visually-hidden">
            {t("planner.pickerSearch")}
          </label>
          <input
            id={searchId}
            type="search"
            className="sheet-search"
            placeholder={t("planner.pickerSearch")}
            value={text}
            autoComplete="off"
            enterKeyHint="search"
            onChange={(e) => setText(e.target.value)}
          />
          {categories.length > 0 && (
            <div className="chip-group" role="group" aria-label={t("planner.pickerCategories")}>
              {categories.map((tag) => (
                <button
                  key={tag.id}
                  type="button"
                  className="chip"
                  aria-pressed={filters.tagIds.includes(tag.id)}
                  onClick={() => setFilters((f) => toggleTag(f, tag.id))}
                >
                  {tag.name}
                </button>
              ))}
            </div>
          )}
          {error && (
            <p role="alert" className="form-error" data-testid="picker-problem">
              {t(error)}
            </p>
          )}
          {list.status === "loading" && <p role="status">{t("planner.pickerLoading")}</p>}
          {list.status === "error" && (
            <div role="alert" className="planner-message">
              <p>{t("planner.pickerLoadFailed")}</p>
              <p>{failureText(t, list.failure)}</p>
              <button type="button" className="button" onClick={retry}>
                {t("planner.retry")}
              </button>
            </div>
          )}
          {list.status === "ok" && list.items.length === 0 && (
            <p className="planner-message">
              {filters.search !== "" || filters.tagIds.length > 0 ? t("planner.pickerNoMatch") : t("planner.pickerEmpty")}
            </p>
          )}
          {list.status === "ok" && list.items.length > 0 && (
            <>
              <ul className="recipe-grid">
                {list.items.map((recipe) => (
                  <li key={recipe.id}>
                    <button
                      type="button"
                      className="recipe-card picker-card"
                      data-testid="picker-card"
                      aria-label={t("planner.pickRecipe", { title: recipe.title })}
                      disabled={adding}
                      onClick={() => void choose(recipe)}
                    >
                      <RecipeImage title={recipe.title} thumbUrl={recipe.thumbUrl} cardUrl={recipe.cardUrl} />
                      <span className="recipe-card-body">
                        <span className="recipe-card-title">{recipe.title}</span>
                      </span>
                    </button>
                  </li>
                ))}
              </ul>
              {list.hasMore && (
                <div className="recipes-more">
                  <button type="button" className="button" disabled={list.loadingMore} onClick={() => void loadMore()}>
                    {t("planner.pickerMore")}
                  </button>
                </div>
              )}
            </>
          )}
        </div>
      </div>
    </div>
  );
}
