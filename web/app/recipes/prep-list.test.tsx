import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { n, t } from "../test-i18n";
import { groupPrepList, prepGroupOf, prepLineText } from "./prep-list";
import { PrepList } from "./prep-list-view";
import type { RecipeIngredient } from "./use-recipe";

const line = (patch: Partial<RecipeIngredient>): RecipeIngredient => ({
  ingredientId: "i",
  ingredientName: "Mel",
  quantity: null,
  unitId: null,
  unitCode: null,
  unitName: null,
  note: null,
  amountKind: "Measured",
  purpose: "Main",
  isOptional: false,
  alternatives: [],
  ...patch,
});

const butter = line({
  ingredientId: "smor",
  ingredientName: "Smør",
  amountKind: "AsNeeded",
  purpose: "Frying",
  alternatives: [
    { ingredientId: "marg", ingredientName: "Margarin" },
    { ingredientId: "olje", ingredientName: "Olje" },
  ],
});
const salt = line({ ingredientId: "salt", ingredientName: "Salt", amountKind: "ToTaste", purpose: "Seasoning" });
const pepper = line({ ingredientId: "pepper", ingredientName: "Pepper", amountKind: "ToTaste", purpose: "Main" });
const flour = line({ ingredientId: "mel", ingredientName: "Mel", quantity: 2, unitCode: "dl", unitId: "u" });
const cream = line({ ingredientId: "romme", ingredientName: "Rømme", amountKind: "AsNeeded", purpose: "Serving" });
const parsley = line({ ingredientId: "persille", ingredientName: "Persille", amountKind: "AsNeeded", purpose: "Garnish", isOptional: true });

describe("grouping the prep list by purpose", () => {
  it("puts a plain to-taste line with the seasoning and keeps the order of lines", () => {
    expect(prepGroupOf(flour)).toBe("main");
    expect(prepGroupOf(pepper)).toBe("seasoning");
    expect(prepGroupOf(butter)).toBe("frying");
    const groups = groupPrepList([parsley, salt, flour, butter, cream, pepper]);
    expect(groups.map((g) => g.group)).toEqual(["main", "frying", "seasoning", "serving", "garnish"]);
    expect(groups.find((g) => g.group === "seasoning")!.lines.map((l) => l.ingredientName)).toEqual(["Salt", "Pepper"]);
  });

  it("leaves empty groups out", () => {
    expect(groupPrepList([flour]).map((g) => g.group)).toEqual(["main"]);
    expect(groupPrepList([])).toEqual([]);
  });

  it("writes a line with its alternatives and optional mark", () => {
    const i18n = { t, formatNumber: (v: number) => n(v) };
    expect(prepLineText(butter, i18n)).toBe(`Smør (${t("recipe.alternativeOr", { names: "Margarin, Olje" })})`);
    expect(prepLineText(parsley, i18n)).toBe(`Persille (${t("recipe.optionalTag")})`);
    expect(prepLineText(flour, i18n)).toBe(`${n(2)} dl Mel`);
  });
});

describe("PrepList", () => {
  it("renders translated headings and the lines of each group", () => {
    render(<PrepList ingredients={[flour, butter, salt, pepper, cream, parsley]} />);

    expect(screen.getByRole("heading", { level: 3, name: t("recipe.prep.frying") })).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 3, name: t("recipe.prep.seasoning") })).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 3, name: t("recipe.prep.serving") })).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 3, name: t("recipe.prep.garnish") })).toBeInTheDocument();

    const frying = within(screen.getByTestId("prep-frying"));
    expect(frying.getByText(/Smør/)).toHaveTextContent(`(${t("recipe.alternativeOr", { names: "Margarin, Olje" })})`);
    const seasoning = within(screen.getByTestId("prep-seasoning"));
    expect(seasoning.getAllByRole("listitem").map((li) => li.textContent?.trim())).toEqual(["Salt", "Pepper"]);
    expect(within(screen.getByTestId("prep-garnish")).getByText(/Persille/)).toHaveTextContent(t("recipe.optionalTag"));
    // The main group has no heading of its own.
    expect(within(screen.getByTestId("prep-main")).queryByRole("heading")).toBeNull();
  });

  it("shows a single list without headings when every line is a main line", () => {
    render(<PrepList ingredients={[flour]} />);
    expect(screen.queryByRole("heading")).toBeNull();
  });
});
