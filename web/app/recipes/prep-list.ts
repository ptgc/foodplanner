import type { I18n, MessageKey } from "../i18n";
import { ingredientAmount } from "./ingredient-amount";
import type { RecipeIngredient } from "./use-recipe";

// The prep list of a recipe (T-076, Q-018): the ingredient lines grouped by what they are for. How an ingredient is used
// stays in the steps; the list only says what to have ready.

export type AmountKind = RecipeIngredient["amountKind"];
export type Purpose = RecipeIngredient["purpose"];

export const amountKinds: readonly AmountKind[] = ["Measured", "ToTaste", "AsNeeded"];
export const purposes: readonly Purpose[] = ["Main", "Frying", "Serving", "Garnish", "Seasoning"];

/** Groups in display order. `main` has no heading (it sits right under "Ingredients"). */
export type PrepGroup = "main" | "frying" | "seasoning" | "serving" | "garnish";

export const prepGroupOrder: readonly PrepGroup[] = ["main", "frying", "seasoning", "serving", "garnish"];

export const prepHeading: Record<Exclude<PrepGroup, "main">, MessageKey> = {
  frying: "recipe.prep.frying",
  seasoning: "recipe.prep.seasoning",
  serving: "recipe.prep.serving",
  garnish: "recipe.prep.garnish",
};

type Grouped = Pick<RecipeIngredient, "amountKind" | "purpose">;

/** The group of a line: its purpose, and a plain "to taste" line (purpose Main) goes with the seasoning. */
export function prepGroupOf(line: Grouped): PrepGroup {
  switch (line.purpose) {
    case "Frying":
      return "frying";
    case "Serving":
      return "serving";
    case "Garnish":
      return "garnish";
    case "Seasoning":
      return "seasoning";
    default:
      return line.amountKind === "ToTaste" ? "seasoning" : "main";
  }
}

/** The lines in groups, in display order; empty groups are left out and the order of lines inside a group is kept. */
export function groupPrepList<T extends Grouped>(lines: readonly T[]): { group: PrepGroup; lines: T[] }[] {
  return prepGroupOrder
    .map((group) => ({ group, lines: lines.filter((line) => prepGroupOf(line) === group) }))
    .filter((g) => g.lines.length > 0);
}

/**
 * The words after the ingredient name, each in its own parentheses: "(eller margarin, olje)", the note, "(valgfritt)" and,
 * outside the groups that already say it, "(etter behov)".
 */
export function prepLineDetails(
  item: Pick<RecipeIngredient, "alternatives" | "note" | "isOptional" | "amountKind" | "purpose">,
  i18n: Pick<I18n, "t">,
): string[] {
  const details: string[] = [];
  if (item.alternatives.length > 0) {
    details.push(i18n.t("recipe.alternativeOr", { names: item.alternatives.map((a) => a.ingredientName).join(", ") }));
  }
  if (item.note) details.push(item.note);
  if (item.amountKind === "AsNeeded" && prepGroupOf(item) === "main") details.push(i18n.t("recipe.asNeeded"));
  if (item.isOptional) details.push(i18n.t("recipe.optionalTag"));
  return details;
}

/** One line as text, for tests and labels: "2 dl melk (eller fløte) (valgfritt)". */
export function prepLineText(item: RecipeIngredient, i18n: Pick<I18n, "t" | "formatNumber">): string {
  const amount = ingredientAmount(item, i18n);
  const details = prepLineDetails(item, i18n).map((d) => `(${d})`);
  return [amount, item.ingredientName, ...details].filter((part) => part !== "").join(" ");
}
