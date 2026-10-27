import { describe, expect, it } from "vitest";
import type { ImportLine } from "./import-api";
import { attentionIndexes, buildLineEdit, confidencePercent, initialLineDraft, lineOfKey, mapImportErrors } from "./import-lines";

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
};

describe("import lines", () => {
  it("lists the lines that are not matched", () => {
    expect(attentionIndexes([{ ...base, status: "Matched" }, base, { ...base, status: "Unparsed" }])).toEqual([1, 2]);
    expect(confidencePercent(base)).toBe(86);
  });

  it("an empty line with no unit word is to taste, an unparsed one is not", () => {
    expect(initialLineDraft(base, String).toTaste).toBe(true);
    expect(initialLineDraft({ ...base, status: "Unparsed" }, String).toTaste).toBe(false);
    expect(initialLineDraft({ ...base, quantity: 3, unitText: "boks" }, String)).toMatchObject({ toTaste: false, quantity: "3" });
  });

  it("builds an edit and refuses incomplete ones", () => {
    const choice = { id: "i1", name: "Melk" };
    expect(buildLineEdit(2, { ingredient: choice, quantity: "1,5", unitId: "u1", toTaste: false, note: " cold " })).toEqual({
      ok: true,
      edit: { position: 2, ingredientId: "i1", quantity: 1.5, unitId: "u1", note: "cold", remove: null },
    });
    expect(buildLineEdit(0, { ingredient: choice, quantity: "", unitId: "", toTaste: true, note: "" })).toMatchObject({
      ok: true,
      edit: { quantity: null, unitId: null },
    });
    const bad = buildLineEdit(0, { ingredient: null, quantity: "abc", unitId: "", toTaste: false, note: "" });
    expect(bad.ok).toBe(false);
    if (!bad.ok) expect(Object.keys(bad.errors).sort()).toEqual(["ingredientId", "quantity"]);
    const noUnit = buildLineEdit(0, { ingredient: choice, quantity: "2", unitId: "", toTaste: false, note: "" });
    if (!noUnit.ok) expect(Object.keys(noUnit.errors)).toEqual(["unitId"]);
  });

  it("maps server error keys to rows and fields", () => {
    const save = mapImportErrors({ "lines[3].ingredientId": ["a"], "lines[1].unitId": ["b"], title: ["c"], "steps[0]": ["d"], other: ["e"] });
    expect(Object.keys(save).sort()).toEqual(["form", "lines[1].unitId", "lines[3].ingredientId", "steps[0].text", "title"]);
    // In an edit request the index is the index in the request; the row is what we sent it for.
    expect(Object.keys(mapImportErrors({ "lines[0].quantity": ["q"] }, 5))).toEqual(["lines[5].quantity"]);
    expect(lineOfKey("lines[5].quantity")).toBe(5);
    expect(lineOfKey("title")).toBeNull();
  });
});
