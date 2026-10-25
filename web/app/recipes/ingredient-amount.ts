import type { I18n } from "../i18n";
import type { RecipeIngredient } from "./use-recipe";

/** "2 dl", "1,5 kg", or just the number; nothing when the quantity is unknown ("to taste"). Display only, no arithmetic. */
export function ingredientAmount(item: RecipeIngredient, i18n: Pick<I18n, "formatNumber">): string {
  if (item.quantity === null || item.quantity === undefined) return "";
  const amount = i18n.formatNumber(Number(item.quantity), { maximumFractionDigits: 2 });
  return item.unitCode ? `${amount} ${item.unitCode}` : amount;
}
