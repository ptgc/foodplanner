import type { OdaAdded } from "./oda";

/** For tests: an added line of the Oda result with the T-052 fields filled in (no ingredient, no choice, no alternatives). */
export function addedLine(over: Partial<OdaAdded> & Pick<OdaAdded, "itemId" | "name" | "productName">): OdaAdded {
  return {
    quantity: 1,
    source: "first-hit",
    ingredientId: null,
    productId: 1,
    hasPreference: false,
    preferenceProblem: null,
    alternatives: [],
    ...over,
  };
}
