import { useState } from "react";
import { Link, useParams } from "react-router";
import { useI18n } from "../i18n";
import { problemKey } from "../i18n/problems";
import { ingredientAmount } from "../recipes/ingredient-amount";
import { isOffline, OfflineNotice } from "../pwa/offline-notice";
import { RecipeImage } from "../recipes/recipe-image";
import { limits } from "../recipes/recipe-form";
import { useRecipe, type Recipe } from "../recipes/use-recipe";
import { useScaledIngredients } from "../recipes/use-scaled-ingredients";
import { canEditRecipes, useActiveRole } from "../recipes/use-catalog";
import { useSession } from "../session";

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
  const state = useRecipe(householdId, id);

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
        {state.message === "unreachable" && isOffline() ? (
          <OfflineNotice />
        ) : (
          <p role="alert">{state.message === "unreachable" ? t("problem.unreachable") : t(problemKey(state.message))}</p>
        )}
      </section>
    );

  return <RecipeArticle householdId={householdId} recipe={state.recipe} back={back} canEdit={canEdit} />;
}

function RecipeArticle({
  householdId,
  recipe,
  back,
  canEdit,
}: {
  householdId: string;
  recipe: Recipe;
  back: React.ReactNode;
  canEdit: boolean;
}) {
  const { t, formatNumber } = useI18n();
  const base = Number(recipe.servings);
  const [servings, setServings] = useState(base);
  const scaled = useScaledIngredients(householdId, recipe, servings);
  const minutesOf = (value: number | string | null | undefined) => (value === null || value === undefined ? null : Number(value));
  const prep = minutesOf(recipe.prepMinutes);
  const cook = minutesOf(recipe.cookMinutes);
  const total = minutesOf(recipe.totalMinutes);

  return (
    <article className="recipe-detail" aria-labelledby="recipe-title">
      <div className="recipe-detail-bar">
        {back}
        <Link
          to={servings === base ? `/recipes/${recipe.id}/cook` : `/recipes/${recipe.id}/cook?servings=${servings}`}
          className="button"
          data-testid="recipe-cook-link"
        >
          {t("cook.start")}
        </Link>
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
      {scaled.failed && <p role="alert">{t("recipe.scaleFailed")}</p>}
      {scaled.loading && <p role="status">{t("cook.updating")}</p>}
      <ul className="recipe-ingredients" aria-busy={scaled.loading} hidden={scaled.failed}>
        {scaled.ingredients.map((item, i) => (
          <li key={`${item.ingredientId}-${i}`}>
            <span className="recipe-amount">{ingredientAmount(item, { formatNumber })}</span> {item.ingredientName}
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
