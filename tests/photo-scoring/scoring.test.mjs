// Tests of the recipe-from-photo eval script (T-118) with a tiny synthetic example set built in a temp folder. No network, no key:
// run with `node --test tests/eval`. The script itself is never run against the real API here.
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, writeFileSync, existsSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import { DEFAULT_CANONICAL, expectedLine, fold, iou, normName, overlap, scoreRecipe } from "../../scripts/recipe-photo-eval.mjs";

const script = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..", "scripts", "recipe-photo-eval.mjs");
const tinyJpeg = Buffer.from("/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=", "base64");

const expected = `
export const source = "Testboka (Testforlaget)";
export const canonical = { "kokte poteter": "Potet" };
export const recipes = [
  { n: 1, title: "Bomba torsk", servings: 2, cook: 20, tags: [], tip: "Bruk dobbel folie i bunnen av kista",
    lines: [["Torsk", 150, "g"], ["Småpoteter", 4, "stk"], ["Salt", null, null, null, { p: "Seasoning" }], ["Smør", null, null, "til steking", { k: "AsNeeded", p: "Frying" }]],
    steps: ["Brett folien til en kiste", "Stek i ovnen i tjue minutter"], crop: [0.1, 0.1, 0.9, 0.9, 0] },
];
`;

const answer = {
  isRecipe: true, readable: true, title: "Bomba Torsk", servings: 2, prepMinutes: null, cookMinutes: 20,
  description: "Bruk dobbel folie i bunnen av kista", sourceBook: "Testboka", rawText: "x",
  steps: ["Brett folien til en kiste", "Stek i ovnen i tjue minutter"],
  ingredients: [
    { original: "150 g torsk", ingredient: "Torsk", amountKind: "Measured", quantity: 150, quantityLow: null, unit: "g", purpose: "Main", isOptional: false, alternatives: [], note: null },
    { original: "4 poteter", ingredient: "Potet", amountKind: "Measured", quantity: 4, quantityLow: null, unit: "stk", purpose: "Main", isOptional: false, alternatives: [], note: "småpoteter" },
    { original: "salt", ingredient: "Salt", amountKind: "ToTaste", quantity: null, quantityLow: null, unit: null, purpose: "Seasoning", isOptional: false, alternatives: [], note: null },
    { original: "smør til steking", ingredient: "Smør", amountKind: "AsNeeded", quantity: null, quantityLow: null, unit: null, purpose: "Frying", isOptional: false, alternatives: [], note: "til steking" },
  ],
  picture: { photoIndex: 0, box: { x: 0.1, y: 0.12, w: 0.8, h: 0.78 }, rotationDegrees: 0 },
  warnings: [],
};

function makeSet() {
  const dir = mkdtempSync(join(tmpdir(), "photo-eval-"));
  const book = join(dir, "recipe-photos", "synthetic");
  mkdirSync(book, { recursive: true });
  writeFileSync(join(book, "expected.mjs"), expected);
  writeFileSync(join(book, "01-page.jpg"), tinyJpeg);
  writeFileSync(join(book, "answers.json"), JSON.stringify({ 1: { recipe: answer, usage: { input_tokens: 5000, output_tokens: 1000 }, durationMs: 1234 } }));
  return dir;
}

// The child never inherits a key or CI from the machine running the tests.
function run(args, env = {}) {
  const base = { ...process.env };
  delete base.CI;
  delete base.ANTHROPIC_API_KEY;
  return spawnSync(process.execPath, [script, ...args], { env: { ...base, ...env }, encoding: "utf8", timeout: 30000 });
}

test("--dry-run scores the stored answers, prints the per-field table and exits 0", () => {
  const dir = makeSet();
  const result = run(["--dry-run", "--book", "synthetic", "--dir", dir, "--report-dir", join(dir, "reports")]);
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /title\s+servings\s+cook\s+description\s+source\s+recall\s+precision/);
  assert.match(result.stdout, /overall mean: 100%/);
  assert.match(result.stdout, /estimated cost \$0\.020/); // 5000 in at $2 + 1000 out at $10 per MTok (the default model is Sonnet 5.5)
  assert.ok(existsSync(join(dir, "reports")));
});

test("without a key and without --dry-run it exits non-zero before any network call", () => {
  const dir = makeSet();
  const result = run(["--book", "synthetic", "--dir", dir]);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /ANTHROPIC_API_KEY/);
});

test("with CI set it refuses, even with a key", () => {
  const dir = makeSet();
  const result = run(["--book", "synthetic", "--dir", dir], { CI: "true", ANTHROPIC_API_KEY: "not-used" });
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /CI/);
});

test("a book without a folder is refused", () => {
  const result = run(["--dry-run", "--book", "does-not-exist", "--dir", makeSet()]);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /no example set/);
});

test("the canonical map lets Småpoteter match Potet, and the default aliases are known", () => {
  assert.equal(normName("Småpoteter", { [fold("småpoteter")]: "Potet" }), normName("Potet", {}));
  assert.equal(DEFAULT_CANONICAL["småpoteter"], "Potet");
  assert.equal(DEFAULT_CANONICAL["pepper"], "Kvernet pepper");
  assert.equal(DEFAULT_CANONICAL["issalat"], "Salat");
  assert.equal(DEFAULT_CANONICAL["hvetetortilla"], "Tortillalefser");
});

test("scoring: a perfect answer scores 1 everywhere and a wrong quantity or purpose lowers only its field", () => {
  const recipe = { n: 1, title: "Bomba torsk", servings: 2, cook: 20, tip: "Bruk dobbel folie i bunnen av kista", steps: ["Brett folien til en kiste"], lines: [["Torsk", 150, "g"], ["Salt", null, null, null, { p: "Seasoning" }]], crop: [0.1, 0.1, 0.9, 0.9, 0] };
  const good = { ...answer, steps: ["Brett folien til en kiste"], ingredients: answer.ingredients.filter((i) => ["Torsk", "Salt"].includes(i.ingredient)) };
  const perfect = scoreRecipe(recipe, good, "Testboka (Testforlaget)", {});
  for (const field of ["title", "servings", "cook", "description", "source", "recall", "precision", "quantity", "unit", "amountKind", "purpose", "optional", "steps", "picture"]) {
    assert.equal(perfect[field], 1, field);
  }

  const bad = { ...good, ingredients: good.ingredients.map((i) => (i.ingredient === "Torsk" ? { ...i, quantity: 200 } : { ...i, purpose: "Main" })) };
  const scored = scoreRecipe(recipe, bad, "Testboka", {});
  assert.equal(scored.quantity, 0.5);
  assert.equal(scored.purpose, 0.5);
  assert.equal(scored.unit, 1);
  assert.equal(scored.recall, 1);
});

test("scoring: extra lines lower precision and missing lines lower recall", () => {
  const recipe = { n: 1, title: "T", servings: null, cook: null, tip: null, steps: [], lines: [["Torsk", 1, "stk"], ["Salt"]] };
  const answerWith = (names) => ({ title: "T", ingredients: names.map((ingredient) => ({ ingredient, quantity: 1, unit: "stk", amountKind: "Measured", purpose: "Main", isOptional: false })), steps: [] });
  const scored = scoreRecipe(recipe, answerWith(["Torsk", "Lakris", "Kaffe"]), "x", {});
  assert.equal(scored.recall, 0.5);
  assert.ok(Math.abs(scored.precision - 1 / 3) < 1e-9);
});

test("helpers: folding, overlap, iou and the default quantity kind", () => {
  assert.equal(fold("Grønnsakbuljong å Æ"), "gronnsakbuljong a ae");
  assert.equal(overlap("a b c d", "a b c x"), 0.75);
  assert.ok(iou({ x: 0, y: 0, w: 1, h: 1 }, { x: 0, y: 0, w: 0.5, h: 1 }) === 0.5);
  assert.equal(expectedLine(["Salt", null, null, null, {}]).amountKind, "ToTaste");
  assert.equal(expectedLine(["Torsk", 150, "g"]).amountKind, "Measured");
});

test("scoring: a null expected cook or servings is not scored, and a printed range accepts any value inside it", () => {
  const recipe = { n: 1, title: "T", servings: null, cook: null, tip: null, steps: [], lines: [["Hvitløk", 3, "fedd"], ["Reker", 200, "g"], ["Ost", 300, "g"]] };
  const line = (ingredient, quantity, quantityLow, unit) => ({ ingredient, quantity, quantityLow, unit, amountKind: "Measured", purpose: "Main", isOptional: false });
  const scored = scoreRecipe(recipe, { title: "T", cookMinutes: 30, servings: 4, steps: [], ingredients: [line("Hvitløk", 4, 2, "fedd"), line("Reke", 200, null, "g"), line("Ost", 400, 350, "g")] }, "x", {});
  assert.equal(scored.cook, null);
  assert.equal(scored.servings, null);
  assert.equal(scored.recall, 1); // "reke" and "reker" are the same word
  assert.ok(Math.abs(scored.quantity - 2 / 3) < 1e-9); // 3 is inside 2-4; 300 is outside 350-400
});
