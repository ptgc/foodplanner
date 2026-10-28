import { describe, expect, it } from "vitest";
import type { ImportLine } from "./import-api";
import { attentionIndexes, buildLineEdit, confidencePercent, initialLineDraft, lineOfKey, mapImportErrors, type LineDraft } from "./import-lines";

const base: ImportLine = {
  position: 0,
  original: "x",
  ingredientText: null,
  quantity: null,
  quantityLow: null,
  unitText: null,
  unitId: null,
  unitCode: null,
  unitName: null,
  ingredientId: null,
  ingredientName: null,
  confidence: 0.857,
  note: null,
  status: "UnmatchedIngredient",
  suggestions: [],
  amountKind: "ToTaste",
  purpose: "Seasoning",
  isOptional: false,
  alternatives: [],
};

const draftBase: Pick<LineDraft, "purpose" | "isOptional" | "alternatives"> = { purpose: "Main", isOptional: false, alternatives: [] };

describe("import lines", () => {
  it("lists the lines that are not matched", () => {
    expect(attentionIndexes([{ ...base, status: "Matched" }, base, { ...base, status: "Unparsed" }])).toEqual([1, 2]);
    expect(confidencePercent(base)).toBe(86);
  });

  it("the editor starts from the kind, purpose, optional flag and alternatives the server read", () => {
    expect(initialLineDraft(base, String)).toMatchObject({ amountKind: "ToTaste", purpose: "Seasoning", isOptional: false });
    expect(initialLineDraft({ ...base, status: "Unparsed", amountKind: "Measured", purpose: "Main" }, String).amountKind).toBe("Measured");
    expect(initialLineDraft({ ...base, amountKind: "Measured", quantity: 3, unitText: "boks" }, String)).toMatchObject({ amountKind: "Measured", quantity: "3" });
    const withAlternative = initialLineDraft(
      { ...base, purpose: "Frying", isOptional: true, alternatives: [{ ingredientId: "a1", ingredientName: "Olje" }] },
      String,
    );
    expect(withAlternative).toMatchObject({ purpose: "Frying", isOptional: true, alternatives: [{ id: "a1", name: "Olje" }] });
  });

  it("builds an edit and refuses incomplete ones", () => {
    const choice = { id: "i1", name: "Melk" };
    expect(buildLineEdit(2, { ...draftBase, ingredient: choice, quantity: "1,5", unitId: "u1", amountKind: "Measured", note: " cold " })).toEqual({
      ok: true,
      edit: {
        position: 2, ingredientId: "i1", quantity: 1.5, unitId: "u1", note: "cold", toTaste: null, remove: null,
        amountKind: "Measured", purpose: "Main", isOptional: false, alternativeIds: [],
      },
    });
    expect(
      buildLineEdit(0, {
        ingredient: choice, quantity: "5", unitId: "u1", amountKind: "AsNeeded", purpose: "Frying", isOptional: true,
        alternatives: [{ id: "a1", name: "Olje" }], note: "",
      }),
    ).toMatchObject({
      ok: true,
      edit: { quantity: null, unitId: null, amountKind: "AsNeeded", purpose: "Frying", isOptional: true, alternativeIds: ["a1"], note: "" },
    });
    const bad = buildLineEdit(0, { ...draftBase, ingredient: null, quantity: "abc", unitId: "", amountKind: "Measured", note: "" });
    expect(bad.ok).toBe(false);
    if (!bad.ok) expect(Object.keys(bad.errors).sort()).toEqual(["ingredientId", "quantity"]);
    const noUnit = buildLineEdit(0, { ...draftBase, ingredient: choice, quantity: "2", unitId: "", amountKind: "Measured", note: "" });
    expect(noUnit.ok).toBe(false);
    if (!noUnit.ok) expect(Object.keys(noUnit.errors)).toEqual(["unitId"]);
  });

  it("maps server error keys to rows and fields", () => {
    const save = mapImportErrors({ "lines[3].ingredientId": ["a"], "lines[1].unitId": ["b"], title: ["c"], "steps[0]": ["d"], other: ["e"] });
    expect(Object.keys(save).sort()).toEqual(["form", "lines[1].unitId", "lines[3].ingredientId", "steps[0].text", "title"]);
    // In an edit request the index is the index in the request; the row is what we sent it for.
    expect(Object.keys(mapImportErrors({ "lines[0].quantity": ["q"] }, 5))).toEqual(["lines[5].quantity"]);
    expect(lineOfKey("lines[5].quantity")).toBe(5);
    expect(lineOfKey("title")).toBeNull();
    expect(mapImportErrors({ "lines[2].alternativeIds[0]": ["x"] })["lines[2].alternativeIds"]?.key).toBe("recipe.error.alternativeUnknown");
  });
});
