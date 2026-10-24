import { useEffect, useState } from "react";
import { Link, useParams } from "react-router";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { useI18n, type I18n } from "../i18n";
import { problemKey, problemTypeOf } from "../i18n/problems";
import { RecipeImage } from "../recipes/recipe-image";
import { limits } from "../recipes/recipe-form";
import { canEditRecipes, useActiveRole } from "../recipes/use-catalog";
import { useSession } from "../session";

type Recipe = components["schemas"]["RecipeResponse"];
type Ingredient = components["schemas"]["RecipeIngredientResponse"];

type State =
  | { kind: "ok"; recipe: Recipe }
  | { kind: "notFound" }
  | { kind: "error"; message: "unreachable" | string | undefined };

/**
 * "2 dl", "1,5 kg", or just the number; nothing when the quantity is unknown ("to taste"). `scale` multiplies the
 * stored quantity for display only (the servings scaler); it never changes what is saved.
 */
export function ingredientAmount(item: Ingredient, i18n: Pick<I18n, "formatNumber">, scale = 1): string {
  if (item.quantity === null || item.quantity === undefined) return "";
  const amount = i18n.formatNumber(Number(item.quantity) * scale, { maximumFractionDigits: 2 });
  return item.unitCode ? `${amount} ${item.unitCode}` : amount;
}

export default function RecipeDetail() {
  const { t } = useI18n();
  const { session } = useSession();
  const { id } = useParams();
  if (session.status !== "signedIn" || !id) return null;
  if (!session.activeHouseholdId) return <p>{t("recipes.noHousehold")}</p>;
  return <RecipeView key={`${session.activeHouseholdId}/${id}`} householdId={session.activeHouseholdId} id={id} />;
}

function RecipeView({ householdId, id }: { householdId: string; id: string }) {
  const { t } = useI18n();
  const canEdit = canEditRecipes(useActiveRole());
  const [state, setState] = useState<State | null>(null);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      let next: State;
      try {
        const { data, error, response } = await api.GET("/api/households/{householdId}/recipes/{recipeId}", {
          params: { path: { householdId, recipeId: id } },
        });
        if (data) next = { kind: "ok", recipe: data };
        else if (response.status === 404) next = { kind: "notFound" };
        else next = { kind: "error", message: problemTypeOf(error) };
      } catch {
        next = { kind: "error", message: "unreachable" };
      }
      if (!cancelled) setState(next);
    })();
    return () => {
      cancelled = true;
    };
  }, [householdId, id]);

  const back = (
    <Link to="/recipes" className="back-link">
      {t("recipes.back")}
    </Link>
  );

  if (!state) return <p role="status">{t("common.loading")}</p>;
  if (state.kind === "notFound")
    return (
      <section className="recipe-detail">
        {back}
        <p role="alert">{t("recipes.notFound")}</p>
      </section>
    );
  if (state.kind === "error")
    return (
      <section className="recipe-detail">
        {back}
        <p role="alert">{state.message === "unreachable" ? t("problem.unreachable") : t(problemKey(state.message))}</p>
      </section>
    );

  return <RecipeArticle recipe={state.recipe} back={back} canEdit={canEdit} />;
}

function RecipeArticle({ recipe, back, canEdit }: { recipe: Recipe; back: React.ReactNode; canEdit: boolean }) {
  const { t, formatNumber } = useI18n();
  const base = Number(recipe.servings);
  const [servings, setServings] = useState(base);
  const scale = servings / base;
  const minutesOf = (value: number | string | null | undefined) => (value === null || value === undefined ? null : Number(value));
  const prep = minutesOf(recipe.prepMinutes);
  const cook = minutesOf(recipe.cookMinutes);
  const total = minutesOf(recipe.totalMinutes);

  return (
    <article className="recipe-detail" aria-labelledby="recipe-title">
      <div className="recipe-detail-bar">
        {back}
        {canEdit && (
          <Link to={`/recipes/${recipe.id}/edit`} className="button" data-testid="recipe-edit-link">
            {t("recipe.edit")}
          </Link>
        )}
      </div>
      <RecipeImage
        title={recipe.title}
        thumbUrl={recipe.image?.thumbUrl}
        cardUrl={recipe.image?.cardUrl}
        fullUrl={recipe.image?.fullUrl}
        sizes="(min-width: 40rem) 40rem, 100vw"
      />
      <h1 id="recipe-title">{recipe.title}</h1>
      {total !== null && (
        <dl className="recipe-times" aria-label={t("recipe.times")}>
          {prep !== null && cook !== null && (
            <>
              <div>
                <dt>{t("recipe.prepTime")}</dt>
                <dd>{t("recipes.minutes", { minutes: prep })}</dd>
              </div>
              <div>
                <dt>{t("recipe.cookTime")}</dt>
                <dd>{t("recipes.minutes", { minutes: cook })}</dd>
              </div>
            </>
          )}
          {(prep === null || cook === null) && (
            <div>
              <dt>{prep !== null ? t("recipe.prepTime") : t("recipe.cookTime")}</dt>
              <dd>{t("recipes.minutes", { minutes: total })}</dd>
            </div>
          )}
          {prep !== null && cook !== null && (
            <div>
              <dt>{t("recipe.totalTime")}</dt>
              <dd>{t("recipes.minutes", { minutes: total })}</dd>
            </div>
          )}
        </dl>
      )}
      {recipe.description && <p className="recipe-description">{recipe.description}</p>}
      {recipe.tags.length > 0 && (
        <p className="recipe-card-tags">
          {recipe.tags.map((tag) => (
            <span key={tag.id} className="tag-pill">
              {tag.name}
            </span>
          ))}
        </p>
      )}

      <div className="servings-scaler" role="group" aria-labelledby="servings-label">
        <span id="servings-label" className="servings-label">
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
        {servings !== base && (
          <button type="button" className="button button-outline" onClick={() => setServings(base)}>
            {t("recipe.servingsReset", { servings: base })}
          </button>
        )}
      </div>
      {servings !== base && <p className="recipe-scaled-note">{t("recipe.servingsScaled", { servings: base })}</p>}

      <h2>{t("recipes.ingredients")}</h2>
      <ul className="recipe-ingredients">
        {recipe.ingredients.map((item, i) => (
          <li key={`${item.ingredientId}-${i}`}>
            <span className="recipe-amount">{ingredientAmount(item, { formatNumber }, scale)}</span> {item.ingredientName}
            {item.note ? ` (${item.note})` : ""}
          </li>
        ))}
      </ul>
      <h2>{t("recipes.steps")}</h2>
      <ol className="recipe-steps">
        {recipe.steps.map((step) => (
          <li key={step.position}>{step.text}</li>
        ))}
      </ol>
    </article>
  );
}
