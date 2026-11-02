import { useState } from "react";
import { useI18n } from "../i18n";
import { RecipeImage } from "../recipes/recipe-image";
import type { RecipeSummary } from "../recipes/use-recipes";
import { HeartIcon } from "./icons";
import { setLike } from "./kid-api";
import { useKidRecipes } from "./kid-recipes";
import { problemKey } from "../i18n/problems";

/** The kid's home (T-091): a vertical feed of big dish photos with a heart on each. No time, tags, servings or steps. */
export function KidHome({ householdId }: { householdId: string }) {
  const { t } = useI18n();
  const { recipes, retry } = useKidRecipes(householdId);

  return (
    <section className="kid-feed" aria-label={t("kid.home.title")} data-testid="kid-home">
      {recipes.status === "loading" && <p role="status">{t("kid.home.loading")}</p>}
      {recipes.status === "error" && (
        <div role="alert" className="planner-message">
          <p>{recipes.unreachable ? t("problem.unreachable") : t(problemKey(recipes.problemType))}</p>
          <button type="button" className="button kid-big-button" onClick={retry}>
            {t("planner.retry")}
          </button>
        </div>
      )}
      {recipes.status === "ok" && recipes.items.length === 0 && <p className="planner-message">{t("kid.home.empty")}</p>}
      {recipes.status === "ok" && recipes.items.length > 0 && (
        <ul className="kid-dishes">
          {recipes.items.map((recipe) => (
            <li key={recipe.id}>
              <KidDish householdId={householdId} recipe={recipe} />
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function KidDish({ householdId, recipe }: { householdId: string; recipe: RecipeSummary }) {
  const { t } = useI18n();
  const [liked, setLiked] = useState(recipe.liked);
  const [pops, setPops] = useState(0);
  const [refused, setRefused] = useState(0);
  const [busy, setBusy] = useState(false);

  const toggle = async () => {
    if (busy) return;
    const next = !liked;
    setLiked(next); // at once: a small child taps and expects the heart to answer
    if (next) setPops((n) => n + 1);
    setBusy(true);
    const result = await setLike(householdId, recipe.id, next);
    setBusy(false);
    if (!result.ok) {
      setLiked(!next); // the server did not take it: back to what is true, with a short shake (no error text)
      setRefused((n) => n + 1);
    }
  };

  return (
    <article className="kid-dish" data-testid="kid-dish" aria-label={recipe.title}>
      <div className="kid-dish-photo">
        <RecipeImage title={recipe.title} thumbUrl={recipe.thumbUrl} cardUrl={recipe.cardUrl} sizes="(min-width: 40rem) 40rem, 100vw" />
        <button
          type="button"
          className={liked ? "kid-heart kid-heart-on" : "kid-heart"}
          aria-pressed={liked}
          aria-label={t("kid.like", { title: recipe.title })}
          data-testid="kid-heart"
          onClick={() => void toggle()}
        >
          {/* The key restarts the CSS animation on every tap. */}
          <span key={`${pops}-${refused}`} className={`kid-heart-glyph${pops > 0 && liked ? " kid-pop" : ""}${refused > 0 ? " kid-shake" : ""}`}>
            <HeartIcon filled={liked} />
          </span>
        </button>
      </div>
      <p className="kid-dish-title">{recipe.title}</p>
    </article>
  );
}
