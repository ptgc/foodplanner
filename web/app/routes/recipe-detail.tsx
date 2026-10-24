import { useEffect, useState } from "react";
import { Link, useParams } from "react-router";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { useI18n, type I18n } from "../i18n";
import { problemKey, problemTypeOf } from "../i18n/problems";
import { RecipeImage } from "../recipes/recipe-image";
import { useSession } from "../session";

type Recipe = components["schemas"]["RecipeResponse"];
type Ingredient = components["schemas"]["RecipeIngredientResponse"];

type State =
  | { kind: "ok"; recipe: Recipe }
  | { kind: "notFound" }
  | { kind: "error"; message: "unreachable" | string | undefined };

/** "2 dl", "1,5 kg", or just the number; nothing when the quantity is unknown ("to taste"). */
export function ingredientAmount(item: Ingredient, i18n: Pick<I18n, "formatNumber">): string {
  if (item.quantity === null || item.quantity === undefined) return "";
  const amount = i18n.formatNumber(Number(item.quantity), { maximumFractionDigits: 2 });
  return item.unitCode ? `${amount} ${item.unitCode}` : amount;
}

/**
 * Minimal read-only recipe page so a tapped card has somewhere to go. T-025 replaces it with the full
 * detail/edit page.
 */
export default function RecipeDetail() {
  const { t } = useI18n();
  const { session } = useSession();
  const { id } = useParams();
  if (session.status !== "signedIn" || !id) return null;
  if (!session.activeHouseholdId) return <p>{t("recipes.noHousehold")}</p>;
  return <RecipeView key={`${session.activeHouseholdId}/${id}`} householdId={session.activeHouseholdId} id={id} />;
}

function RecipeView({ householdId, id }: { householdId: string; id: string }) {
  const { t, formatNumber } = useI18n();
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

  const { recipe } = state;
  const minutes = recipe.totalMinutes === null || recipe.totalMinutes === undefined ? null : Number(recipe.totalMinutes);
  return (
    <article className="recipe-detail" aria-labelledby="recipe-title">
      {back}
      <RecipeImage title={recipe.title} thumbUrl={recipe.image?.thumbUrl} cardUrl={recipe.image?.cardUrl} sizes="(min-width: 40rem) 40rem, 100vw" />
      <h1 id="recipe-title">{recipe.title}</h1>
      <p className="recipe-meta">
        {minutes !== null && <span>{t("recipes.minutes", { minutes })}</span>}
        <span>{t("recipes.servings", { count: Number(recipe.servings) })}</span>
      </p>
      {recipe.description && <p>{recipe.description}</p>}
      {recipe.tags.length > 0 && (
        <p className="recipe-card-tags">
          {recipe.tags.map((tag) => (
            <span key={tag.id} className="tag-pill">
              {tag.name}
            </span>
          ))}
        </p>
      )}
      <h2>{t("recipes.ingredients")}</h2>
      <ul className="recipe-ingredients">
        {recipe.ingredients.map((item, i) => (
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
