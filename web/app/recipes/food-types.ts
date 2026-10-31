import type { components } from "../api/schema";
import type { MessageKey } from "../i18n";

export type FoodType = components["schemas"]["FoodType"];
export type ProteinClass = components["schemas"]["ProteinClass"];

/** The food types a recipe can have (T-089), in the order pickers and chips show them. */
export const FOOD_TYPES: readonly FoodType[] = [
  "Fish",
  "Seafood",
  "Chicken",
  "Beef",
  "Pork",
  "Lamb",
  "MixedMeat",
  "Vegetarian",
  "Vegan",
  "Unknown",
];

/** The protein classes an adult can give an own ingredient. Unknown means "not set yet". */
export const PROTEIN_CLASSES: readonly ProteinClass[] = [
  "Unknown",
  "None",
  "AnimalProduct",
  "Fish",
  "Seafood",
  "Chicken",
  "Beef",
  "Pork",
  "Lamb",
  "Meat",
];

export const foodTypeKey = (type: FoodType): MessageKey => `foodType.${type}` as MessageKey;
export const proteinClassKey = (value: ProteinClass): MessageKey => `proteinClass.${value}` as MessageKey;

const known = new Set<string>(FOOD_TYPES);
export const isFoodType = (value: string | null | undefined): value is FoodType => value != null && known.has(value);
