import type { AmountKind, Purpose } from "../recipes/prep-list";
import { limits, parseQuantity, type FieldErrors, type IngredientChoice } from "../recipes/recipe-form";
import type { ImportLine, LineEdit } from "./import-api";

/** A line is done when the server says it is matched; everything else must be fixed before the draft can be saved. */
export const needsAttention = (line: ImportLine): boolean => line.status !== "Matched";

/** Indexes of the lines that need attention, in order. */
export const attentionIndexes = (lines: readonly ImportLine[]): number[] =>
  lines.flatMap((line, index) => (needsAttention(line) ? [index] : []));

/** Match confidence 0..1 as a whole percent. */
export const confidencePercent = (line: Pick<ImportLine, "confidence">): number => Math.round(Number(line.confidence) * 100);

/** What the inline editor of one line holds. */
export type LineDraft = {
  ingredient: IngredientChoice | null;
  /** As typed ("1,5" and "1.5" both work). Ignored unless `amountKind` is Measured. */
  quantity: string;
  unitId: string;
  amountKind: AmountKind;
  purpose: Purpose;
  isOptional: boolean;
  alternatives: IngredientChoice[];
  note: string;
};

/** The editor's starting values from what the server parsed. `format` writes a number in the active locale. */
export function initialLineDraft(line: ImportLine, format: (value: number) => string): LineDraft {
  const quantity = line.quantity === null ? null : Number(line.quantity);
  return {
    ingredient: line.ingredientId ? { id: line.ingredientId, name: line.ingredientName ?? "" } : null,
    quantity: quantity === null ? "" : format(quantity),
    unitId: line.unitId ?? "",
    // The server read the kind from the phrases ("etter smak", "til steking"); a line that could not be read is Measured.
    amountKind: line.amountKind,
    purpose: line.purpose,
    isOptional: line.isOptional,
    alternatives: line.alternatives.map((a) => ({ id: a.ingredientId, name: a.ingredientName })),
    note: line.note ?? "",
  };
}

export type BuiltEdit = { ok: true; edit: LineEdit } | { ok: false; errors: FieldErrors };

/**
 * The edit to send for one line (the server replaces the line's whole structured part), or the local field errors.
 * Keys are `ingredientId`, `quantity`, `unitId`, `note`; the caller prefixes them with the row.
 */
export function buildLineEdit(position: number, draft: LineDraft): BuiltEdit {
  const errors: FieldErrors = {};
  if (!draft.ingredient) errors.ingredientId = { key: "import.error.lineIngredient" };
  let quantity: number | null = null;
  const measured = draft.amountKind === "Measured";
  if (measured) {
    quantity = parseQuantity(draft.quantity);
    if (quantity === null || quantity <= 0 || quantity > limits.quantityMax) {
      errors.quantity = { key: "recipe.error.quantity", params: { max: limits.quantityMax, decimals: limits.quantityDecimals } };
    } else if (draft.unitId === "") {
      errors.unitId = { key: "import.error.lineUnit" };
    }
  }
  if (draft.note.trim().length > limits.noteMax) errors.note = { key: "recipe.error.note", params: { max: limits.noteMax } };
  if (Object.keys(errors).length > 0 || !draft.ingredient) return { ok: false, errors };
  return {
    ok: true,
    edit: {
      position,
      ingredientId: draft.ingredient.id,
      quantity: measured ? quantity : null,
      unitId: measured ? draft.unitId : null,
      // An empty note clears it; null would keep the parsed one.
      note: draft.note.trim(),
      amountKind: draft.amountKind,
      purpose: draft.purpose,
      isOptional: draft.isOptional,
      alternativeIds: draft.alternatives.map((a) => a.id),
      toTaste: null,
      remove: null,
    },
  };
}

export const removalEdit = (position: number): LineEdit => ({
  position,
  ingredientId: null,
  quantity: null,
  unitId: null,
  note: null,
  remove: true,
  toTaste: null,
});

/**
 * Maps the API's ValidationProblem `errors` keys to translated field errors on the review screen. Server texts are
 * English and never shown. `lines[i]` keys: in a PUT, `i` is the index in the request's `lines` (we send one edit, so
 * `lineIndex` says which row it was); in a save, `i` is the row itself (`lineIndex` is undefined). Result keys are
 * `lines[row].field`, `title`, `servings`, `prepMinutes`, `cookMinutes`, `steps[i].text`, or `form` for anything else.
 */
export function mapImportErrors(serverErrors: Record<string, string[]>, lineIndex?: number): FieldErrors {
  const out: FieldErrors = {};
  for (const key of Object.keys(serverErrors)) {
    const line = /^lines\[(\d+)\](?:\.(\w+)(?:\[\d+\])?)?$/.exec(key);
    if (line) {
      const row = lineIndex ?? Number(line[1]);
      const at = `lines[${row}]`;
      switch (line[2]) {
        case "alternativeIds":
          out[`${at}.alternativeIds`] = { key: "recipe.error.alternativeUnknown" };
          break;
        case "ingredientId":
          out[`${at}.ingredientId`] = { key: "import.error.lineIngredient" };
          break;
        case "unitId":
          out[`${at}.unitId`] = { key: "import.error.lineUnit" };
          break;
        case "quantity":
          out[`${at}.quantity`] = { key: "recipe.error.quantity", params: { max: limits.quantityMax, decimals: limits.quantityDecimals } };
          break;
        case "note":
          out[`${at}.note`] = { key: "recipe.error.note", params: { max: limits.noteMax } };
          break;
        default:
          out[`${at}.ingredientId`] = { key: "recipe.error.unknown" };
      }
      continue;
    }
    const step = /^steps\[(\d+)\](?:\.text)?$/.exec(key);
    if (step) {
      out[`steps[${step[1]}].text`] = { key: "recipe.error.step", params: { max: limits.stepMax } };
    } else if (key === "steps") {
      out[key] = { key: "recipe.error.steps", params: { max: limits.stepsMax } };
    } else if (key === "title") {
      out[key] = { key: "recipe.error.title", params: { max: limits.titleMax } };
    } else if (key === "description") {
      out[key] = { key: "recipe.error.description", params: { max: limits.descriptionMax } };
    } else if (key === "servings") {
      out[key] = { key: "recipe.error.servings", params: { min: limits.servingsMin, max: limits.servingsMax } };
    } else if (key === "prepMinutes" || key === "cookMinutes") {
      out[key] = { key: "recipe.error.minutes", params: { max: limits.minutesMax } };
    } else if (key === "lines") {
      out.form = { key: "recipe.error.ingredients", params: { max: limits.ingredientsMax } };
    } else {
      out.form = { key: "recipe.error.unknown" };
    }
  }
  return out;
}

/** The row an error key belongs to (`lines[2].unitId` gives 2), or null. */
export function lineOfKey(key: string): number | null {
  const m = /^lines\[(\d+)\]/.exec(key);
  return m ? Number(m[1]) : null;
}
