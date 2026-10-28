import type { components } from "../api/schema";
import type { MessageKey, Params } from "../i18n";
import type { AmountKind, Purpose } from "./prep-list";
import { safeSourceUrl, sourceUrlMax } from "./source-link";

// Request/response shapes come from the generated client; nothing is hand-written here.
type Recipe = components["schemas"]["RecipeResponse"];
export type RecipeBody = components["schemas"]["RecipeRequest"];
export type UnitDimension = components["schemas"]["UnitDimension"];

/** The limits of the API (T-021, T-022). The server stays the authority; these only give quick feedback. */
export const limits = {
  titleMax: 200,
  descriptionMax: 2000,
  servingsMin: 1,
  servingsMax: 100,
  minutesMax: 1440,
  stepsMax: 50,
  stepMax: 2000,
  ingredientsMax: 100,
  alternativesMax: 5,
  noteMax: 200,
  quantityMax: 100000,
  quantityDecimals: 4,
  tagsMax: 20,
  /** Recipe.MaxSourceNameLength (the column is nvarchar(100)). */
  sourceNameMax: 100,
  imageMaxBytes: 10 * 1024 * 1024,
} as const;

export const imageTypes = ["image/jpeg", "image/png", "image/webp"] as const;

export type IngredientChoice = { id: string; name: string; dimension?: UnitDimension };

export type IngredientRow = {
  /** Stable React key; not sent to the API. */
  key: string;
  ingredient: IngredientChoice | null;
  /** What was typed ("1,5" and "1.5" both work). Ignored unless `amountKind` is Measured. */
  quantity: string;
  unitId: string;
  /** Measured has a quantity and a unit; ToTaste and AsNeeded have neither (T-076). */
  amountKind: AmountKind;
  purpose: Purpose;
  isOptional: boolean;
  /** Other ingredients that can stand in (catalog or own, never free text). */
  alternatives: IngredientChoice[];
  note: string;
};

export type StepRow = { key: string; text: string };

export type FormState = {
  title: string;
  description: string;
  servings: string;
  prepMinutes: string;
  cookMinutes: string;
  tagIds: string[];
  ingredients: IngredientRow[];
  steps: StepRow[];
  /** A website (link + optional name, T-075) or a book/other source (name only, T-081). */
  sourceKind: SourceKind;
  /** The link of a website source; always empty for a book. */
  sourceUrl: string;
  /** The website's optional name (blank = the API derives the host), or the whole text of a book/other source. */
  sourceName: string;
};

export type SourceKind = "website" | "book";

/** A translated message to show on a field: the key and its parameters. */
export type FieldError = { key: MessageKey; params?: Params };
/** Keyed like the API's ValidationProblem `errors`: `title`, `ingredients[2].unitId`, `tagIds[0]`, `file`, ... */
export type FieldErrors = Record<string, FieldError>;

let counter = 0;
export function newKey(): string {
  counter += 1;
  return `row-${counter}`;
}

export const emptyIngredient = (): IngredientRow => ({
  key: newKey(),
  ingredient: null,
  quantity: "",
  unitId: "",
  amountKind: "Measured",
  purpose: "Main",
  isOptional: false,
  alternatives: [],
  note: "",
});

export const emptyStep = (): StepRow => ({ key: newKey(), text: "" });

export const emptyForm = (): FormState => ({
  title: "",
  description: "",
  servings: "4",
  prepMinutes: "",
  cookMinutes: "",
  tagIds: [],
  ingredients: [emptyIngredient()],
  steps: [emptyStep()],
  sourceKind: "website",
  sourceUrl: "",
  sourceName: "",
});

/** A quantity as typed: "1.5" or "1,5" (nb), up to 4 decimals. Returns null when it is not a plain number. */
export function parseQuantity(text: string): number | null {
  const normalized = text.trim().replace(",", ".");
  if (!new RegExp(`^(\\d+|\\d*\\.\\d{1,${limits.quantityDecimals}})$`).test(normalized)) return null;
  return Number(normalized);
}

function parseWhole(text: string): number | null {
  const trimmed = text.trim();
  return /^\d+$/.test(trimmed) ? Number(trimmed) : null;
}

const hasControlChars = (value: string) => /\p{Cc}/u.test(value);

/** The same checks as the API (RecipeEndpoints.Validate), field by field. Empty result = the form may be sent. */
export function validate(form: FormState): FieldErrors {
  const errors: FieldErrors = {};

  const title = form.title.trim();
  if (title.length < 1 || title.length > limits.titleMax || hasControlChars(title)) {
    errors.title = { key: "recipe.error.title", params: { max: limits.titleMax } };
  }
  if (form.description.trim().length > limits.descriptionMax) {
    errors.description = { key: "recipe.error.description", params: { max: limits.descriptionMax } };
  }
  const servings = parseWhole(form.servings);
  if (servings === null || servings < limits.servingsMin || servings > limits.servingsMax) {
    errors.servings = { key: "recipe.error.servings", params: { min: limits.servingsMin, max: limits.servingsMax } };
  }
  for (const field of ["prepMinutes", "cookMinutes"] as const) {
    const text = form[field].trim();
    if (text === "") continue; // optional
    const minutes = parseWhole(text);
    if (minutes === null || minutes > limits.minutesMax) {
      errors[field] = { key: "recipe.error.minutes", params: { max: limits.minutesMax } };
    }
  }

  if (form.sourceKind === "website" && form.sourceUrl.trim() !== "" && safeSourceUrl(form.sourceUrl.trim()) === null) {
    errors.sourceUrl = { key: "recipe.error.sourceUrl", params: { max: sourceUrlMax } };
  }
  const sourceName = form.sourceName.trim();
  if (sourceName.length > limits.sourceNameMax || hasControlChars(sourceName)) {
    errors.sourceName = { key: "recipe.error.sourceName", params: { max: limits.sourceNameMax } };
  }

  if (form.tagIds.length > limits.tagsMax) {
    errors.tagIds = { key: "recipe.error.tags", params: { max: limits.tagsMax } };
  }

  if (form.ingredients.length > limits.ingredientsMax) {
    errors.ingredients = { key: "recipe.error.ingredients", params: { max: limits.ingredientsMax } };
  } else {
    form.ingredients.forEach((row, i) => {
      const at = `ingredients[${i}]`;
      if (!row.ingredient) errors[`${at}.ingredientId`] = { key: "recipe.error.ingredientRequired" };
      if (row.alternatives.length > limits.alternativesMax) {
        errors[`${at}.alternativeIds`] = { key: "recipe.error.alternatives", params: { max: limits.alternativesMax } };
      }
      if (row.amountKind === "Measured") {
        const quantity = parseQuantity(row.quantity);
        if (quantity === null || quantity <= 0 || quantity > limits.quantityMax) {
          errors[`${at}.quantity`] = {
            key: "recipe.error.quantity",
            params: { max: limits.quantityMax, decimals: limits.quantityDecimals },
          };
        }
        // Quantity and unit come together: a unit is needed for any amount.
        if (row.unitId === "") errors[`${at}.unitId`] = { key: "recipe.error.unitRequired" };
      }
      if (row.note.trim().length > limits.noteMax) {
        errors[`${at}.note`] = { key: "recipe.error.note", params: { max: limits.noteMax } };
      }
    });
  }

  if (form.steps.length > limits.stepsMax) {
    errors.steps = { key: "recipe.error.steps", params: { max: limits.stepsMax } };
  } else {
    form.steps.forEach((step, i) => {
      const text = step.text.trim();
      if (text.length < 1 || text.length > limits.stepMax) {
        errors[`steps[${i}].text`] = { key: "recipe.error.step", params: { max: limits.stepMax } };
      }
    });
  }

  return errors;
}

/**
 * A website sends the link and its optional name (a name without a link is dropped); a book sends the name only, never a link.
 * Both blank clears the source.
 */
function sourceRequest(form: FormState): { sourceUrl: string | null; sourceName: string | null } {
  const name = form.sourceName.trim() === "" ? null : form.sourceName.trim();
  if (form.sourceKind === "book") return { sourceUrl: null, sourceName: name };
  const url = form.sourceUrl.trim() === "" ? null : form.sourceUrl.trim();
  return { sourceUrl: url, sourceName: url === null ? null : name };
}

/** The request body for a form that passed `validate`. Blank optional fields become null. */
export function toRequest(form: FormState): RecipeBody {
  const blankToNull = (value: string) => (value.trim() === "" ? null : Number(value.trim()));
  return {
    title: form.title.trim(),
    description: form.description.trim() === "" ? null : form.description.trim(),
    servings: Number(form.servings.trim()),
    prepMinutes: blankToNull(form.prepMinutes),
    cookMinutes: blankToNull(form.cookMinutes),
    tagIds: form.tagIds,
    ingredients: form.ingredients.map((row) => ({
      ingredientId: row.ingredient?.id ?? null,
      quantity: row.amountKind === "Measured" ? parseQuantity(row.quantity) : null,
      unitId: row.amountKind === "Measured" ? row.unitId : null,
      note: row.note.trim() === "" ? null : row.note.trim(),
      amountKind: row.amountKind,
      purpose: row.purpose,
      isOptional: row.isOptional,
      alternativeIds: row.alternatives.map((a) => a.id),
    })),
    steps: form.steps.map((step) => ({ text: step.text.trim() })),
    ...sourceRequest(form),
  };
}

/** A quantity for an input box, in the decimal style of the language ("1,5" in nb). */
export function quantityText(value: number, decimalComma: boolean): string {
  const text = String(value);
  return decimalComma ? text.replace(".", ",") : text;
}

/** The form for editing an existing recipe. */
export function fromRecipe(recipe: Recipe, decimalComma: boolean): FormState {
  return {
    title: recipe.title,
    description: recipe.description ?? "",
    servings: String(Number(recipe.servings)),
    prepMinutes: recipe.prepMinutes === null || recipe.prepMinutes === undefined ? "" : String(Number(recipe.prepMinutes)),
    cookMinutes: recipe.cookMinutes === null || recipe.cookMinutes === undefined ? "" : String(Number(recipe.cookMinutes)),
    tagIds: recipe.tags.map((tag) => tag.id),
    ingredients: recipe.ingredients.map((line) => {
      const measured = line.amountKind === "Measured" && line.quantity !== null && line.quantity !== undefined;
      return {
        key: newKey(),
        ingredient: { id: line.ingredientId, name: line.ingredientName },
        quantity: measured ? quantityText(Number(line.quantity), decimalComma) : "",
        unitId: line.unitId ?? "",
        amountKind: line.amountKind,
        purpose: line.purpose,
        isOptional: line.isOptional,
        alternatives: line.alternatives.map((a) => ({ id: a.ingredientId, name: a.ingredientName })),
        note: line.note ?? "",
      };
    }),
    steps: [...recipe.steps].sort((a, b) => Number(a.position) - Number(b.position)).map((s) => ({ key: newKey(), text: s.text })),
    // A name without a link is a book or other source (T-081).
    sourceKind: recipe.sourceUrl ? "website" : recipe.sourceName ? "book" : "website",
    sourceUrl: recipe.sourceUrl ?? "",
    sourceName: recipe.sourceName ?? "",
  };
}

/**
 * After a language switch: the form keeps everything the user typed and only takes the ingredient names (rows and
 * alternatives) from the recipe as the API now names them in the new language, matched by ingredient id (T-078).
 * Rows the recipe does not know (added but not saved) keep the name they were picked with. Returns the same form
 * when no name changed.
 */
export function renameIngredients(form: FormState, recipe: Recipe): FormState {
  const names = new Map<string, string>();
  for (const line of recipe.ingredients) {
    names.set(line.ingredientId, line.ingredientName);
    for (const a of line.alternatives) names.set(a.ingredientId, a.ingredientName);
  }
  let changed = false;
  const rename = (choice: IngredientChoice): IngredientChoice => {
    const name = names.get(choice.id);
    if (name === undefined || name === choice.name) return choice;
    changed = true;
    return { ...choice, name };
  };
  const ingredients = form.ingredients.map((row) => ({
    ...row,
    ingredient: row.ingredient ? rename(row.ingredient) : null,
    alternatives: row.alternatives.map(rename),
  }));
  return changed ? { ...form, ingredients } : form;
}

/** A new array with the item at `from` moved to `to` (the same array if that is not possible). */
export function move<T>(list: readonly T[], from: number, to: number): T[] {
  if (from === to || from < 0 || to < 0 || from >= list.length || to >= list.length) return [...list];
  const next = [...list];
  const [item] = next.splice(from, 1);
  next.splice(to, 0, item as T);
  return next;
}

/** The `errors` object of a ValidationProblem body, if the body has one. */
export function serverErrorsOf(body: unknown): Record<string, string[]> | undefined {
  if (typeof body !== "object" || body === null) return undefined;
  const errors = (body as { errors?: unknown }).errors;
  if (typeof errors !== "object" || errors === null) return undefined;
  const result: Record<string, string[]> = {};
  for (const [key, value] of Object.entries(errors)) result[key] = Array.isArray(value) ? value.map(String) : [];
  return Object.keys(result).length > 0 ? result : undefined;
}

/**
 * Maps the API's ValidationProblem `errors` keys to translated field errors. The server texts are English and never
 * shown; the key says which field, and the form state says which of the field's rules was broken (for `unitId`: not
 * given, not allowed, or an id the server does not know). A key that matches no field lands on `form`.
 */
export function mapServerErrors(serverErrors: Record<string, string[]>, form: FormState): FieldErrors {
  const out: FieldErrors = {};
  for (const key of Object.keys(serverErrors)) {
    const alternative = /^ingredients\[(\d+)\]\.alternativeIds(\[\d+\])?$/.exec(key);
    if (alternative) {
      out[key] = alternative[2]
        ? { key: "recipe.error.alternativeUnknown" }
        : { key: "recipe.error.alternatives", params: { max: limits.alternativesMax } };
      continue;
    }
    const line = /^ingredients\[(\d+)\]\.(ingredientId|quantity|unitId|note|amountKind|purpose)$/.exec(key);
    if (line) {
      const row = form.ingredients[Number(line[1])];
      switch (line[2]) {
        case "ingredientId":
          out[key] = { key: row?.ingredient ? "recipe.error.ingredientUnknown" : "recipe.error.ingredientRequired" };
          break;
        case "quantity":
          out[key] = { key: "recipe.error.quantity", params: { max: limits.quantityMax, decimals: limits.quantityDecimals } };
          break;
        case "unitId":
          out[key] = {
            key: row && row.amountKind !== "Measured" ? "recipe.error.unitNotAllowed" : row?.unitId === "" ? "recipe.error.unitRequired" : "recipe.error.unitUnknown",
          };
          break;
        case "amountKind":
        case "purpose":
          out[key] = { key: "recipe.error.unknown" };
          break;
        default:
          out[key] = { key: "recipe.error.note", params: { max: limits.noteMax } };
      }
      continue;
    }
    if (/^ingredients(\[\d+\])?$/.test(key)) {
      out[key] = { key: "recipe.error.ingredients", params: { max: limits.ingredientsMax } };
    } else if (/^steps\[\d+\]\.text$/.test(key)) {
      out[key] = { key: "recipe.error.step", params: { max: limits.stepMax } };
    } else if (key === "steps") {
      out[key] = { key: "recipe.error.steps", params: { max: limits.stepsMax } };
    } else if (key === "tagIds") {
      out[key] = { key: "recipe.error.tags", params: { max: limits.tagsMax } };
    } else if (/^tagIds\[\d+\]$/.test(key)) {
      out[key] = { key: "recipe.error.tagUnknown" };
    } else if (key === "title") {
      out[key] = { key: "recipe.error.title", params: { max: limits.titleMax } };
    } else if (key === "description") {
      out[key] = { key: "recipe.error.description", params: { max: limits.descriptionMax } };
    } else if (key === "servings") {
      out[key] = { key: "recipe.error.servings", params: { min: limits.servingsMin, max: limits.servingsMax } };
    } else if (key === "prepMinutes" || key === "cookMinutes") {
      out[key] = { key: "recipe.error.minutes", params: { max: limits.minutesMax } };
    } else if (key === "sourceUrl") {
      out.sourceUrl = { key: "recipe.error.sourceUrl", params: { max: sourceUrlMax } };
    } else if (key === "sourceName") {
      out.sourceName = { key: "recipe.error.sourceName", params: { max: limits.sourceNameMax } };
    } else if (key === "file") {
      out[key] = { key: "recipe.error.imageInvalid" };
    } else {
      out.form = { key: "recipe.error.unknown" };
    }
  }
  return out;
}

/** The DOM id of the field for an error key, used by the error summary links and `aria-describedby`. */
export function fieldId(key: string): string {
  return `recipe-field-${key.replace(/[^A-Za-z0-9]+/g, "-").replace(/-$/, "")}`;
}

const rank = (key: string): number => {
  const order = ["title", "description", "servings", "prepMinutes", "cookMinutes", "tagIds", "ingredients", "steps", "sourceUrl", "sourceName", "file", "form"];
  const index = order.findIndex((name) => key === name || key.startsWith(`${name}[`));
  return index === -1 ? order.length : index;
};

/** Error keys in the order the fields appear on the page (rows in order), for the summary. */
export function orderedKeys(errors: FieldErrors): string[] {
  const rowOf = (key: string) => Number(/\[(\d+)\]/.exec(key)?.[1] ?? -1);
  return Object.keys(errors).sort((a, b) => rank(a) - rank(b) || rowOf(a) - rowOf(b));
}

/** Image checks before upload: JPEG, PNG or WebP, at most 10 MB. The server decides from the bytes anyway. */
export function checkImage(file: { type: string; size: number }): FieldError | null {
  if (!(imageTypes as readonly string[]).includes(file.type)) return { key: "recipe.error.imageType" };
  if (file.size > limits.imageMaxBytes) return { key: "recipe.error.imageSize", params: { max: limits.imageMaxBytes / (1024 * 1024) } };
  return null;
}
