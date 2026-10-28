import { describe, expect, it } from "vitest";
import type { components } from "../api/schema";
import {
  checkImage,
  emptyForm,
  emptyIngredient,
  emptyStep,
  fieldId,
  fromRecipe,
  limits,
  mapServerErrors,
  move,
  orderedKeys,
  parseQuantity,
  serverErrorsOf,
  toRequest,
  validate,
  type FormState,
  type IngredientRow,
} from "./recipe-form";

type Recipe = components["schemas"]["RecipeResponse"];

const line = (patch: Partial<IngredientRow> = {}): IngredientRow => ({
  ...emptyIngredient(),
  ingredient: { id: "i1", name: "Torsk" },
  quantity: "400",
  unitId: "u-g",
  ...patch,
});

const valid = (patch: Partial<FormState> = {}): FormState => ({
  ...emptyForm(),
  title: "Fiskesuppe",
  ingredients: [line()],
  steps: [{ ...emptyStep(), text: "Kok opp vannet" }],
  ...patch,
});

describe("validate (the same limits as the API)", () => {
  it("accepts a complete form", () => {
    expect(validate(valid())).toEqual({});
  });

  it("title: 1 to 200 characters, no control characters", () => {
    expect(validate(valid({ title: "   " })).title).toBeDefined();
    expect(validate(valid({ title: "a".repeat(201) })).title).toBeDefined();
    expect(validate(valid({ title: "a".repeat(200) })).title).toBeUndefined();
    expect(validate(valid({ title: "a\u0007b" })).title).toBeDefined();
  });

  it("description: at most 2000 characters", () => {
    expect(validate(valid({ description: "d".repeat(2001) })).description).toBeDefined();
    expect(validate(valid({ description: "d".repeat(2000) })).description).toBeUndefined();
  });

  it("servings: a whole number from 1 to 100", () => {
    for (const bad of ["", "0", "101", "2.5", "-1", "abc"]) {
      expect(validate(valid({ servings: bad })).servings, bad).toBeDefined();
    }
    for (const ok of ["1", "100", " 4 "]) {
      expect(validate(valid({ servings: ok })).servings, ok).toBeUndefined();
    }
  });

  it("minutes: empty or a whole number from 0 to 1440", () => {
    expect(validate(valid({ prepMinutes: "", cookMinutes: "0" }))).toEqual({});
    expect(validate(valid({ prepMinutes: "1440" }))).toEqual({});
    expect(validate(valid({ prepMinutes: "1441" })).prepMinutes).toBeDefined();
    expect(validate(valid({ cookMinutes: "-5" })).cookMinutes).toBeDefined();
    expect(validate(valid({ cookMinutes: "1.5" })).cookMinutes).toBeDefined();
  });

  it("steps: at most 50, each 1 to 2000 characters, error keyed by position", () => {
    const errors = validate(valid({ steps: [{ ...emptyStep(), text: "ok" }, { ...emptyStep(), text: " " }, { ...emptyStep(), text: "x".repeat(2001) }] }));
    expect(Object.keys(errors)).toEqual(["steps[1].text", "steps[2].text"]);
    const many = Array.from({ length: 51 }, () => ({ ...emptyStep(), text: "x" }));
    expect(validate(valid({ steps: many })).steps).toBeDefined();
    expect(validate(valid({ steps: many.slice(0, 50) }))).toEqual({});
  });

  it("ingredient lines: at most 100, an ingredient is required", () => {
    const many = Array.from({ length: 101 }, () => line());
    expect(validate(valid({ ingredients: many })).ingredients).toBeDefined();
    expect(validate(valid({ ingredients: many.slice(0, 100) }))).toEqual({});
    expect(validate(valid({ ingredients: [line({ ingredient: null })] }))["ingredients[0].ingredientId"]).toBeDefined();
  });

  it("quantity: above 0, at most 100000, at most 4 decimals", () => {
    const check = (quantity: string) => validate(valid({ ingredients: [line({ quantity })] }))["ingredients[0].quantity"];
    for (const bad of ["", "0", "0.0", "100000.0001", "100001", "0.00001", "-1", "1e3", "abc"]) {
      expect(check(bad), bad).toBeDefined();
    }
    for (const ok of ["0.0001", "1,5", "1.5", "100000", "250", ".5"]) {
      expect(check(ok), ok).toBeUndefined();
    }
  });

  it("quantity and unit go together: an amount needs a unit, to taste has neither", () => {
    const noUnit = validate(valid({ ingredients: [line({ unitId: "" })] }));
    expect(Object.keys(noUnit)).toEqual(["ingredients[0].unitId"]);
    const toTaste = validate(valid({ ingredients: [line({ amountKind: "ToTaste", quantity: "", unitId: "" })] }));
    expect(toTaste).toEqual({});
    // A leftover quantity or unit on a to-taste line is ignored, never sent.
    const body = toRequest(valid({ ingredients: [line({ amountKind: "AsNeeded", quantity: "5", unitId: "u-g" })] }));
    expect(body.ingredients?.[0]).toMatchObject({ quantity: null, unitId: null });
  });

  it("note: at most 200 characters", () => {
    expect(validate(valid({ ingredients: [line({ note: "n".repeat(201) })] }))["ingredients[0].note"]).toBeDefined();
    expect(validate(valid({ ingredients: [line({ note: "n".repeat(200) })] }))).toEqual({});
  });

  it("tags: at most 20", () => {
    expect(validate(valid({ tagIds: Array.from({ length: 21 }, (_, i) => `t${i}`) })).tagIds).toBeDefined();
  });
});

describe("parseQuantity", () => {
  it("reads both decimal separators", () => {
    expect(parseQuantity("1,5")).toBe(1.5);
    expect(parseQuantity(" 2.25 ")).toBe(2.25);
    expect(parseQuantity("1,2,3")).toBeNull();
  });
});

describe("toRequest", () => {
  it("trims, turns blanks into null and keeps the order of lines and steps", () => {
    const body = toRequest(
      valid({
        title: "  Suppe ",
        description: "  ",
        servings: " 6 ",
        prepMinutes: "",
        cookMinutes: "30",
        tagIds: ["a", "b"],
        ingredients: [line({ quantity: "1,5", note: " finhakket " }), line({ ingredient: { id: "i2", name: "Salt" }, amountKind: "ToTaste" })],
        steps: [{ ...emptyStep(), text: " Ett " }, { ...emptyStep(), text: "To" }],
      }),
    );
    expect(body).toEqual({
      title: "Suppe",
      description: null,
      servings: 6,
      prepMinutes: null,
      cookMinutes: 30,
      tagIds: ["a", "b"],
      ingredients: [
        { ingredientId: "i1", quantity: 1.5, unitId: "u-g", note: "finhakket", amountKind: "Measured", purpose: "Main", isOptional: false, alternativeIds: [] },
        { ingredientId: "i2", quantity: null, unitId: null, note: null, amountKind: "ToTaste", purpose: "Main", isOptional: false, alternativeIds: [] },
      ],
      steps: [{ text: "Ett" }, { text: "To" }],
      sourceUrl: null,
      sourceName: null,
    });
  });
});

describe("kind, purpose, optional and alternatives (T-076)", () => {
  it("sends them, and no amount for a line that is not measured", () => {
    const body = toRequest(
      valid({
        ingredients: [
          line({
            amountKind: "AsNeeded", purpose: "Frying", isOptional: true, quantity: "", unitId: "",
            alternatives: [{ id: "a1", name: "Margarin" }, { id: "a2", name: "Olje" }],
          }),
        ],
      }),
    );
    expect(body.ingredients?.[0]).toEqual({
      ingredientId: "i1", quantity: null, unitId: null, note: null,
      amountKind: "AsNeeded", purpose: "Frying", isOptional: true, alternativeIds: ["a1", "a2"],
    });
  });

  it("allows at most 5 alternatives", () => {
    const many = Array.from({ length: 6 }, (_, i) => ({ id: `a${i}`, name: `A${i}` }));
    expect(validate(valid({ ingredients: [line({ alternatives: many })] }))["ingredients[0].alternativeIds"]).toBeDefined();
  });

  it("maps the server's alternative errors", () => {
    const mapped = mapServerErrors(
      { "ingredients[0].alternativeIds[1]": ["x"], "ingredients[0].alternativeIds": ["y"] },
      valid(),
    );
    expect(mapped["ingredients[0].alternativeIds[1]"]?.key).toBe("recipe.error.alternativeUnknown");
    expect(mapped["ingredients[0].alternativeIds"]?.key).toBe("recipe.error.alternatives");
  });
});

describe("mapServerErrors", () => {
  it("puts each ValidationProblem key on the matching field, with our own messages", () => {
    const form = valid({
      ingredients: [line(), line({ unitId: "u-foreign" })],
      tagIds: ["tag-ok", "tag-foreign"],
    });
    const mapped = mapServerErrors(
      {
        title: ["The title must be 1 to 200 characters"],
        "ingredients[1].unitId": ["The unit does not exist."],
        "ingredients[0].ingredientId": ["The ingredient does not exist."],
        "tagIds[1]": ["The tag does not exist."],
        "steps[0].text": ["x"],
        servings: ["x"],
        file: ["x"],
      },
      form,
    );
    expect(Object.keys(mapped).sort()).toEqual(
      ["file", "ingredients[0].ingredientId", "ingredients[1].unitId", "servings", "steps[0].text", "tagIds[1]", "title"].sort(),
    );
    expect(mapped["ingredients[1].unitId"]?.key).toBe("recipe.error.unitUnknown");
    expect(mapped["ingredients[0].ingredientId"]?.key).toBe("recipe.error.ingredientUnknown");
    expect(mapped["tagIds[1]"]?.key).toBe("recipe.error.tagUnknown");
    expect(mapped.file?.key).toBe("recipe.error.imageInvalid");
  });

  it("chooses the unit message from the state of the line (needed, not allowed, unknown)", () => {
    const form = valid({
      ingredients: [line({ unitId: "" }), line({ amountKind: "ToTaste", quantity: "", unitId: "" }), line()],
    });
    const mapped = mapServerErrors({ "ingredients[0].unitId": ["a"], "ingredients[1].unitId": ["b"], "ingredients[2].unitId": ["c"] }, form);
    expect(mapped["ingredients[0].unitId"]?.key).toBe("recipe.error.unitRequired");
    expect(mapped["ingredients[1].unitId"]?.key).toBe("recipe.error.unitNotAllowed");
    expect(mapped["ingredients[2].unitId"]?.key).toBe("recipe.error.unitUnknown");
  });

  it("collects keys that match no field under `form`", () => {
    expect(mapServerErrors({ body: ["A request body is required."] }, valid()).form?.key).toBe("recipe.error.unknown");
  });

  it("reads the errors object of a ValidationProblem body and ignores other bodies", () => {
    expect(serverErrorsOf({ errors: { title: ["x"] } })).toEqual({ title: ["x"] });
    expect(serverErrorsOf({ type: "urn:foodplanner:problem:forbidden" })).toBeUndefined();
    expect(serverErrorsOf(null)).toBeUndefined();
    expect(serverErrorsOf({ errors: {} })).toBeUndefined();
  });
});

describe("move and orderedKeys", () => {
  it("moves an item and leaves the list alone when the move is impossible", () => {
    expect(move(["a", "b", "c"], 2, 0)).toEqual(["c", "a", "b"]);
    expect(move(["a", "b", "c"], 0, 1)).toEqual(["b", "a", "c"]);
    expect(move(["a", "b"], 0, -1)).toEqual(["a", "b"]);
    expect(move(["a", "b"], 1, 2)).toEqual(["a", "b"]);
  });

  it("lists errors in page order, rows in order", () => {
    const e = { key: "recipe.error.title" as const };
    const keys = orderedKeys({ "steps[0].text": e, "ingredients[10].unitId": e, "ingredients[2].quantity": e, tagIds: e, title: e, file: e });
    expect(keys).toEqual(["title", "tagIds", "ingredients[2].quantity", "ingredients[10].unitId", "steps[0].text", "file"]);
  });

  it("gives element ids that are safe to use", () => {
    expect(fieldId("ingredients[2].unitId")).toBe("recipe-field-ingredients-2-unitId");
  });
});

describe("checkImage", () => {
  it("accepts JPEG, PNG and WebP up to 10 MB", () => {
    expect(checkImage({ type: "image/png", size: 1000 })).toBeNull();
    expect(checkImage({ type: "image/webp", size: limits.imageMaxBytes })).toBeNull();
    expect(checkImage({ type: "image/gif", size: 1000 })?.key).toBe("recipe.error.imageType");
    expect(checkImage({ type: "image/jpeg", size: limits.imageMaxBytes + 1 })?.key).toBe("recipe.error.imageSize");
  });
});

describe("fromRecipe", () => {
  it("fills the form from the API response, ordering steps by position and marking to-taste lines", () => {
    const recipe: Recipe = {
      id: "r1",
      sourceUrl: "https://www.matprat.no/x",
      sourceName: "matprat.no",
      title: "Suppe",
      description: null,
      servings: 4,
      prepMinutes: 10,
      cookMinutes: null,
      totalMinutes: 10,
      image: null,
      tags: [{ id: "t1", name: "Rask", kind: "Tag", isGlobal: true }],
      steps: [
        { position: 2, text: "To" },
        { position: 1, text: "Ett" },
      ],
      ingredients: [
        { ingredientId: "i1", ingredientName: "Torsk", quantity: 1.5, unitId: "u-kg", unitCode: "kg", unitName: "kilogram", note: null, amountKind: "Measured", purpose: "Main", isOptional: false, alternatives: [] },
        { ingredientId: "i2", ingredientName: "Salt", quantity: null, unitId: null, unitCode: null, unitName: null, note: "etter smak", amountKind: "ToTaste", purpose: "Seasoning", isOptional: true, alternatives: [{ ingredientId: "a1", ingredientName: "Pepper" }] },
      ],
    };
    const form = fromRecipe(recipe, true);
    expect(form).toMatchObject({ title: "Suppe", description: "", servings: "4", prepMinutes: "10", cookMinutes: "", tagIds: ["t1"], sourceUrl: "https://www.matprat.no/x", sourceName: "matprat.no" });
    expect(form.steps.map((s) => s.text)).toEqual(["Ett", "To"]);
    expect(form.ingredients[0]).toMatchObject({ quantity: "1,5", unitId: "u-kg", amountKind: "Measured", purpose: "Main", isOptional: false, alternatives: [] });
    expect(form.ingredients[1]).toMatchObject({ quantity: "", unitId: "", amountKind: "ToTaste", purpose: "Seasoning", isOptional: true, alternatives: [{ id: "a1", name: "Pepper" }], note: "etter smak" });
    expect(validate(form)).toEqual({});
  });
});

describe("recipe source (T-081)", () => {
  const base = { ...emptyForm(), title: "Suppe", ingredients: [], steps: [] };
  const body = (patch: Partial<FormState>) => toRequest({ ...base, ...patch });

  it("a website sends link and optional name; a name without a link is dropped", () => {
    expect(body({ sourceKind: "website", sourceUrl: " https://a.no/x ", sourceName: " Blogg " })).toMatchObject({ sourceUrl: "https://a.no/x", sourceName: "Blogg" });
    expect(body({ sourceKind: "website", sourceUrl: "https://a.no/x" })).toMatchObject({ sourceUrl: "https://a.no/x", sourceName: null });
    expect(body({ sourceKind: "website", sourceUrl: "", sourceName: "Blogg" })).toMatchObject({ sourceUrl: null, sourceName: null });
  });

  it("a book sends the name only, never a link, and blank clears", () => {
    expect(body({ sourceKind: "book", sourceUrl: "https://a.no/x", sourceName: "Kokebok" })).toMatchObject({ sourceUrl: null, sourceName: "Kokebok" });
    expect(body({ sourceKind: "book", sourceName: "  " })).toMatchObject({ sourceUrl: null, sourceName: null });
  });

  it("limits the name to 100 characters and refuses control characters", () => {
    expect(validate({ ...base, sourceKind: "book", sourceName: "n".repeat(100) })).toEqual(expect.not.objectContaining({ sourceName: expect.anything() }));
    expect(validate({ ...base, sourceKind: "book", sourceName: "n".repeat(101) }).sourceName).toBeDefined();
    expect(validate({ ...base, sourceKind: "book", sourceName: "a\u0007b" }).sourceName).toBeDefined();
  });
});
