import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router";
import { useI18n } from "../i18n";
import { problemKey } from "../i18n/problems";
import { isOffline, OfflineNotice } from "../pwa/offline-notice";
import { ingredientAmount } from "../recipes/ingredient-amount";
import { limits } from "../recipes/recipe-form";
import { useRecipe, type Recipe } from "../recipes/use-recipe";
import { useScaledIngredients } from "../recipes/use-scaled-ingredients";
import { useWakeLock } from "../recipes/use-wake-lock";
import { useSession } from "../session";

/** A horizontal swipe at least this long (px), clearly more sideways than up/down, changes the step. */
export const swipeThreshold = 50;

export default function RecipeCook() {
  const { t } = useI18n();
  const { session } = useSession();
  const { id } = useParams();
  if (session.status !== "signedIn" || !id) return null;
  if (!session.activeHouseholdId) return <p>{t("recipes.noHousehold")}</p>;
  return <CookLoader key={`${session.activeHouseholdId}/${id}`} householdId={session.activeHouseholdId} id={id} />;
}

function CookLoader({ householdId, id }: { householdId: string; id: string }) {
  const { t } = useI18n();
  const state = useRecipe(householdId, id);
  const back = (
    <Link to={`/recipes/${id}`} className="back-link">
      {t("cook.backToRecipe")}
    </Link>
  );
  if (!state) return <p role="status">{t("common.loading")}</p>;
  if (state.kind === "notFound")
    return (
      <section className="cook">
        {back}
        <p role="alert">{t("recipes.notFound")}</p>
      </section>
    );
  if (state.kind === "error")
    return (
      <section className="cook">
        {back}
        {state.message === "unreachable" && isOffline() ? (
          <OfflineNotice />
        ) : (
          <p role="alert">{state.message === "unreachable" ? t("problem.unreachable") : t(problemKey(state.message))}</p>
        )}
      </section>
    );
  return <Cooking householdId={householdId} recipe={state.recipe} />;
}

function initialServings(requested: string | null, base: number): number {
  const n = requested !== null && /^\d{1,3}$/.test(requested) ? Number(requested) : base;
  return n >= limits.servingsMin && n <= limits.servingsMax ? n : base;
}

function Cooking({ householdId, recipe }: { householdId: string; recipe: Recipe }) {
  const { t, formatNumber } = useI18n();
  const [search] = useSearchParams();
  const base = Number(recipe.servings);
  const [servings, setServings] = useState(() => initialServings(search.get("servings"), base));
  const [index, setIndex] = useState(0);
  const [showIngredients, setShowIngredients] = useState(false);
  const total = recipe.steps.length;
  const done = index >= total;
  const scaled = useScaledIngredients(householdId, recipe, servings);
  const wakeLock = useWakeLock(!done);
  const doneHeading = useRef<HTMLHeadingElement>(null);
  const touchStart = useRef<{ x: number; y: number } | null>(null);

  const go = useCallback(
    (delta: number) => setIndex((i) => Math.min(total, Math.max(0, i + delta))),
    [total],
  );

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
      const target = event.target as HTMLElement | null;
      if (target && /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName)) return;
      if (event.key === "ArrowRight") go(1);
      else if (event.key === "ArrowLeft") go(-1);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [go]);

  useEffect(() => {
    if (done) doneHeading.current?.focus();
  }, [done]);

  const onTouchStart = (event: React.TouchEvent) => {
    const touch = event.touches[0];
    touchStart.current = touch ? { x: touch.clientX, y: touch.clientY } : null;
  };
  const onTouchEnd = (event: React.TouchEvent) => {
    const start = touchStart.current;
    const touch = event.changedTouches[0];
    touchStart.current = null;
    if (!start || !touch) return;
    const dx = touch.clientX - start.x;
    const dy = touch.clientY - start.y;
    if (Math.abs(dx) >= swipeThreshold && Math.abs(dx) > Math.abs(dy) * 1.5) go(dx < 0 ? 1 : -1);
  };

  const exit = (
    <Link to={`/recipes/${recipe.id}`} className="button button-outline cook-exit" data-testid="cook-exit">
      {t("cook.exit")}
    </Link>
  );
  const wakeHint =
    wakeLock === "unsupported" ? t("cook.wakeLockUnsupported") : wakeLock === "failed" ? t("cook.wakeLockFailed") : null;

  if (done) {
    return (
      <section className="cook cook-done" aria-labelledby="cook-done-title">
        <h1 id="cook-done-title" ref={doneHeading} tabIndex={-1}>
          {t("cook.done")}
        </h1>
        <p className="cook-done-text">{total > 0 ? t("cook.doneText", { total }) : t("cook.noSteps")}</p>
        <div className="cook-actions">
          {total > 0 && (
            <button type="button" className="button button-outline cook-nav" onClick={() => setIndex(total - 1)}>
              {t("cook.backToSteps")}
            </button>
          )}
          <Link to={`/recipes/${recipe.id}`} className="button cook-nav" data-testid="cook-exit">
            {t("cook.backToRecipe")}
          </Link>
        </div>
      </section>
    );
  }

  const step = recipe.steps[index]!;
  return (
    <section className="cook" aria-labelledby="cook-title" onTouchStart={onTouchStart} onTouchEnd={onTouchEnd}>
      <header className="cook-header">
        <h1 id="cook-title" className="cook-title">
          {recipe.title}
        </h1>
        {exit}
      </header>
      {wakeHint && (
        <p className="cook-hint" role="status" data-testid="wake-lock-hint">
          {wakeHint}
        </p>
      )}

      <div className="cook-step" aria-live="polite" aria-atomic="true" data-testid="cook-step">
        <p className="cook-progress" data-testid="cook-progress">
          {t("cook.stepOf", { current: index + 1, total })}
        </p>
        <p className="cook-text">{step.text}</p>
      </div>
      <progress className="cook-bar" max={total} value={index + 1} aria-hidden="true" />

      <div className="cook-actions">
        <button type="button" className="button button-outline cook-nav" disabled={index === 0} onClick={() => go(-1)}>
          {t("cook.previous")}
        </button>
        <button type="button" className="button cook-nav" onClick={() => go(1)}>
          {index === total - 1 ? t("cook.finish") : t("cook.next")}
        </button>
      </div>
      <p className="cook-nav-hint">{t("cook.navigationHint")}</p>

      <button
        type="button"
        className="button button-outline cook-toggle"
        aria-expanded={showIngredients}
        aria-controls="cook-ingredients"
        onClick={() => setShowIngredients((v) => !v)}
      >
        {showIngredients ? t("cook.hideIngredients") : t("cook.showIngredients")}
      </button>
      <div id="cook-ingredients" className="cook-ingredients" hidden={!showIngredients}>
        <h2>{t("cook.ingredients", { servings })}</h2>
        <div className="servings-scaler" role="group" aria-labelledby="cook-servings-label">
          <span id="cook-servings-label" className="servings-label">
            {t("recipe.servingsFor")}
          </span>
          <button
            type="button"
            className="button button-outline stepper"
            aria-label={t("recipe.servingsFewer")}
            disabled={servings <= limits.servingsMin}
            onClick={() => setServings((s) => Math.max(limits.servingsMin, s - 1))}
          >
            &minus;
          </button>
          <output className="servings-value" aria-live="polite" data-testid="servings-value">
            {formatNumber(servings, { useGrouping: false })}
          </output>
          <button
            type="button"
            className="button button-outline stepper"
            aria-label={t("recipe.servingsMore")}
            disabled={servings >= limits.servingsMax}
            onClick={() => setServings((s) => Math.min(limits.servingsMax, s + 1))}
          >
            +
          </button>
        </div>
        {scaled.failed && <p role="alert">{t("recipe.scaleFailed")}</p>}
        {scaled.loading && <p role="status">{t("cook.updating")}</p>}
        <ul className="recipe-ingredients cook-ingredient-list" aria-busy={scaled.loading} hidden={scaled.failed}>
          {scaled.ingredients.map((item, i) => (
            <li key={`${item.ingredientId}-${i}`}>
              <span className="recipe-amount">{ingredientAmount(item, { formatNumber })}</span> {item.ingredientName}
              {item.note ? ` (${item.note})` : ""}
            </li>
          ))}
        </ul>
      </div>
    </section>
  );
}
