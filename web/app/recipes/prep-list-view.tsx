import { useI18n } from "../i18n";
import { ingredientAmount } from "./ingredient-amount";
import { groupPrepList, prepHeading, prepLineDetails } from "./prep-list";
import type { RecipeIngredient } from "./use-recipe";

type Props = {
  ingredients: readonly RecipeIngredient[];
  busy?: boolean;
  hidden?: boolean;
  /** Extra class for the lists (the cooking view makes the text bigger). */
  className?: string;
};

/**
 * The ingredient lines grouped by purpose (T-076): the main lines first, then "Til steking", "Etter smak", "Til servering"
 * and "Til pynt", each with a translated heading. Used by the recipe page and the cooking view.
 */
export function PrepList({ ingredients, busy, hidden, className }: Props) {
  const i18n = useI18n();
  const { t } = i18n;
  const groups = groupPrepList(ingredients);
  return (
    <div className="prep-list" aria-busy={busy} hidden={hidden}>
      {groups.map(({ group, lines }) => (
        <section key={group} className="prep-group" data-testid={`prep-${group}`}>
          {group !== "main" && <h3 className="prep-heading">{t(prepHeading[group])}</h3>}
          <ul className={`recipe-ingredients${className ? ` ${className}` : ""}`}>
            {lines.map((item, i) => (
              <li key={`${item.ingredientId}-${i}`}>
                <span className="recipe-amount">{ingredientAmount(item, i18n)}</span> {item.ingredientName}
                {prepLineDetails(item, i18n).map((detail) => (
                  <span key={detail}> ({detail})</span>
                ))}
              </li>
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
}
