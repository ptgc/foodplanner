import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { searchDelayMs } from "../recipes/ingredient-picker";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import RecipeDetail from "./recipe-detail";
import RecipeEdit from "./recipe-edit";
import Recipes from "./recipes";

type Recipe = components["schemas"]["RecipeResponse"];
type Role = components["schemas"]["HouseholdRole"];
type Ingredient = components["schemas"]["IngredientResponse"];

const me = { userId: "11111111-1111-1111-1111-111111111111", email: "adult@example.test" };
const urn = (code: string) => `urn:foodplanner:problem:${code}`;

const units = [
  { id: "u-g", code: "g", name: "gram", dimension: "Mass", factorToBase: 1 },
  { id: "u-dl", code: "dl", name: "desiliter", dimension: "Volume", factorToBase: 100 },
  { id: "u-stk", code: "stk", name: "stykk", dimension: "Count", factorToBase: 1 },
];
const catalog: Ingredient[] = [
  { id: "i-torsk", name: "Torsk", isStaple: false, dimension: "Mass", isGlobal: true },
  { id: "i-salt", name: "Salt", isStaple: true, dimension: "Mass", isGlobal: true },
  { id: "i-melk", name: "Melk", isStaple: false, dimension: "Volume", isGlobal: true },
];
const tags = [
  { id: "t-dinner", name: "Middag", kind: "Category", isGlobal: true },
  { id: "t-quick", name: "Rask", kind: "Tag", isGlobal: true },
];

const existing = (patch: Partial<Recipe> = {}): Recipe => ({
  sourceUrl: null,
  sourceName: null,
  id: "r1",
  title: "Fiskesuppe",
  description: "God suppe",
  servings: 4,
  prepMinutes: 10,
  cookMinutes: 30,
  totalMinutes: 40,
  image: null,
  tags: [tags[0]!] as Recipe["tags"],
  steps: [
    { position: 1, text: "Kok opp vannet" },
    { position: 2, text: "Ha i fisken" },
  ],
  ingredients: [
    { ingredientId: "i-torsk", ingredientName: "Torsk", quantity: 400, unitId: "u-g", unitCode: "g", unitName: "gram", note: null, amountKind: "Measured", purpose: "Main", isOptional: false, alternatives: [] },
    { ingredientId: "i-salt", ingredientName: "Salt", quantity: null, unitId: null, unitCode: null, unitName: null, note: "etter smak", amountKind: "ToTaste", purpose: "Seasoning", isOptional: false, alternatives: [] },
  ],
  ...patch,
});

type Server = {
  role: Role;
  recipe: Recipe | null;
  /** Bodies of POST/PUT recipe requests, in order. */
  saved: { method: string; path: string; body: Record<string, unknown> }[];
  saveStatus: number;
  saveBody: unknown;
  deleteStatus: number;
  deleteType: string | undefined;
  calls: string[];
  /** The `servings` query of every GET .../scaled request. */
  scaledCalls: string[];
  scaleStatus: number;
  createdIngredients: unknown[];
  ingredientCreateStatus: number;
};
let server: Server;

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });

beforeEach(() => {
  server = {
    role: "Adult",
    recipe: existing(),
    saved: [],
    saveStatus: 0,
    saveBody: null,
    deleteStatus: 204,
    deleteType: undefined,
    calls: [],
    scaledCalls: [],
    scaleStatus: 200,
    createdIngredients: [],
    ingredientCreateStatus: 201,
  };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const url = new URL(request.url);
      const path = url.pathname;
      const key = `${request.method} ${path}`;
      server.calls.push(key);
      if (path === "/api/auth/me") return json(me);
      if (path === "/api/auth/antiforgery") return json({ token: "tok" });
      if (path === "/api/households") return json([{ id: "h1", name: "Home", role: server.role }]);
      if (path === "/api/units") return json(units);
      if (path === "/api/households/h1/tags") return json(tags);
      if (path === "/api/households/h1/ingredients") {
        if (request.method === "POST") {
          const body = (await request.json()) as { name: string; dimension: Ingredient["dimension"]; isStaple: boolean };
          server.createdIngredients.push(body);
          if (server.ingredientCreateStatus !== 201) return json({ type: urn("ingredient-exists"), status: 409 }, 409);
          return json({ id: "i-new", name: body.name, isStaple: body.isStaple, dimension: body.dimension, isGlobal: false }, 201);
        }
        const term = (url.searchParams.get("search") ?? "").toLowerCase();
        return json(catalog.filter((i) => i.name.toLowerCase().includes(term)));
      }
      if (path === "/api/households/h1/recipes" && request.method === "GET") return json({ items: [], total: 0, limit: 12, offset: 0 });
      if (path === "/api/households/h1/recipes" && request.method === "POST") {
        server.saved.push({ method: "POST", path, body: (await request.json()) as Record<string, unknown> });
        if (server.saveStatus) return json(server.saveBody, server.saveStatus);
        return json({ ...existing(), id: "new1" }, 201);
      }
      const scaled = /^\/api\/households\/h1\/recipes\/([^/]+)\/scaled$/.exec(path);
      if (scaled) {
        const servings = Number(url.searchParams.get("servings"));
        server.scaledCalls.push(url.searchParams.get("servings") ?? "");
        if (server.scaleStatus !== 200) return json({ status: server.scaleStatus }, server.scaleStatus);
        // Stands in for the Domain scaling the real API does (tested in the API and Domain tests).
        const base = server.recipe!;
        return json({
          recipeId: base.id,
          baseServings: base.servings,
          servings,
          ingredients: base.ingredients.map((i) => (i.quantity === null ? i : { ...i, quantity: (Number(i.quantity) * servings) / Number(base.servings) })),
        });
      }
      const recipe = /^\/api\/households\/h1\/recipes\/([^/]+)$/.exec(path);
      if (recipe) {
        if (request.method === "PUT") {
          server.saved.push({ method: "PUT", path, body: (await request.json()) as Record<string, unknown> });
          if (server.saveStatus) return json(server.saveBody, server.saveStatus);
          return json({ ...existing(), id: recipe[1] });
        }
        if (request.method === "DELETE") {
          if (server.deleteStatus === 204) return new Response(null, { status: 204 });
          return json({ type: server.deleteType, status: server.deleteStatus }, server.deleteStatus);
        }
        return server.recipe && server.recipe.id === recipe[1] ? json(server.recipe) : json({ status: 404 }, 404);
      }
      if (/\/recipes\/[^/]+\/image$/.test(path) && request.method === "DELETE") return new Response(null, { status: 204 });
      throw new Error(`unexpected ${key}`);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function Probe() {
  const location = useLocation();
  return <output data-testid="url">{location.pathname}</output>;
}

function renderAt(url: string) {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <SessionProvider>
        <Probe />
        <Routes>
          <Route path="/recipes" element={<Recipes />} />
          <Route path="/recipes/new" element={<RecipeEdit />} />
          <Route path="/recipes/:id" element={<RecipeDetail />} />
          <Route path="/recipes/:id/edit" element={<RecipeEdit />} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

const url = () => screen.getByTestId("url").textContent;
const rows = () => screen.getAllByTestId("ingredient-row");
const steps = () => screen.getAllByTestId("step-row");
const stepTexts = () => steps().map((s) => (within(s).getByRole("textbox") as HTMLTextAreaElement).value);
const save = () => screen.getByRole("button", { name: t("recipe.save") });

async function formReady(path = "/recipes/new") {
  renderAt(path);
  await screen.findByRole("heading", { level: 1, name: path.endsWith("edit") ? t("recipe.editTitle") : t("recipe.newTitle") });
  // Units and tags load separately from the form itself.
  await screen.findByRole("button", { name: "Middag" });
  await waitFor(() => expect(within(rows()[0]!).getByLabelText(t("recipe.unit"))).toBeInTheDocument());
}

async function pick(user: ReturnType<typeof userEvent.setup>, index: number, term: string, name: string) {
  await user.type(within(rows()[index]!).getByLabelText(t("recipe.ingredientSearch")), term);
  await user.click(await within(rows()[index]!).findByRole("button", { name }, { timeout: searchDelayMs + 2000 }));
}

async function selectUnit(user: ReturnType<typeof userEvent.setup>, index: number, id: string) {
  const select = within(rows()[index]!).getByLabelText(t("recipe.unit"));
  await waitFor(() => expect(within(select).getAllByRole("option").length).toBeGreaterThan(1));
  await user.selectOptions(select, id);
}

describe("who sees edit controls (Child vs Adult)", () => {
  it("a Child gets no form on /recipes/new and /recipes/:id/edit, and nothing is requested", async () => {
    server.role = "Child";
    renderAt("/recipes/new");
    expect(await screen.findByTestId("no-access")).toHaveTextContent(t("recipe.noAccess"));
    expect(screen.queryByRole("textbox")).toBeNull();
    expect(screen.queryByRole("button", { name: t("recipe.save") })).toBeNull();
    expect(server.calls.some((c) => c.startsWith("POST") && c.includes("/recipes"))).toBe(false);
  });

  it("a Child sees neither the edit page nor delete for an existing recipe", async () => {
    server.role = "Child";
    renderAt("/recipes/r1/edit");
    expect(await screen.findByTestId("no-access")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: t("recipe.delete") })).toBeNull();
    expect(screen.queryByLabelText(t("recipe.pictureFile"))).toBeNull();
  });

  it("the detail page and the grid show Edit and New recipe to an Adult and an Owner, not to a Child", async () => {
    for (const [role, visible] of [
      ["Child", false],
      ["Adult", true],
      ["Owner", true],
    ] as const) {
      server.role = role;
      const detail = renderAt("/recipes/r1");
      await screen.findByRole("heading", { level: 1, name: "Fiskesuppe" });
      expect(screen.queryByTestId("recipe-edit-link") !== null, `${role} detail`).toBe(visible);
      detail.unmount();

      const grid = renderAt("/recipes");
      await screen.findByTestId("recipes-empty");
      expect(screen.queryByTestId("recipe-new-link") !== null, `${role} grid`).toBe(visible);
      grid.unmount();
    }
  });

  it("an Adult gets the form with a labelled field for everything", async () => {
    await formReady();
    for (const label of ["recipe.fieldTitle", "recipe.fieldDescription", "recipe.fieldServings", "recipe.fieldPrep", "recipe.fieldCook", "recipe.pictureFile"] as const) {
      expect(screen.getByLabelText(t(label))).toBeInTheDocument();
    }
    expect(screen.getByRole("group", { name: t("recipe.tagsHeading") })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: t("recipe.delete") })).toBeNull(); // new recipe: nothing to delete
  });
});

describe("client-side validation blocks the request", () => {
  it("shows an error summary (focused) and a message on each field, and sends nothing", async () => {
    const user = userEvent.setup();
    await formReady();
    await user.click(save());

    const summary = await screen.findByTestId("error-summary");
    expect(summary).toHaveFocus();
    expect(within(summary).getByText(t("recipe.errorSummary", { count: 5 }))).toBeInTheDocument();
    expect(server.saved).toEqual([]);

    const title = screen.getByLabelText(t("recipe.fieldTitle"));
    expect(title).toHaveAttribute("aria-invalid", "true");
    expect(title).toHaveAccessibleDescription(t("recipe.error.title", { max: 200 }));
    expect(within(rows()[0]!).getByText(t("recipe.error.ingredientRequired"))).toBeInTheDocument();
    expect(within(steps()[0]!).getByText(t("recipe.error.step", { max: 2000 }))).toBeInTheDocument();

    // A summary entry takes the user to its field.
    await user.click(within(summary).getByRole("button", { name: new RegExp(t("recipe.error.title", { max: 200 }).slice(0, 12)) }));
    expect(title).toHaveFocus();
  });

  it("an amount without a unit is refused on the unit field; to taste needs neither", async () => {
    const user = userEvent.setup();
    await formReady();
    await user.type(screen.getByLabelText(t("recipe.fieldTitle")), "Suppe");
    await pick(user, 0, "tor", "Torsk");
    await user.type(within(rows()[0]!).getByLabelText(t("recipe.quantity")), "2");
    await user.type(within(steps()[0]!).getByRole("textbox"), "Kok");
    await user.click(save());
    expect(within(rows()[0]!).getByText(t("recipe.error.unitRequired"))).toBeInTheDocument();
    expect(server.saved).toEqual([]);

    await user.selectOptions(within(rows()[0]!).getByLabelText(t("recipe.amountKind")), "ToTaste");
    expect(within(rows()[0]!).queryByLabelText(t("recipe.quantity"))).toBeNull();
    expect(within(rows()[0]!).queryByLabelText(t("recipe.unit"))).toBeNull();
    await user.click(save());
    await waitFor(() => expect(server.saved).toHaveLength(1));
    expect(server.saved[0]!.body.ingredients).toEqual([{ ingredientId: "i-torsk", quantity: null, unitId: null, note: null, amountKind: "ToTaste", purpose: "Main", isOptional: false, alternativeIds: [] }]);
  });

  it("rejects out-of-range numbers with the API's limits", async () => {
    const user = userEvent.setup();
    await formReady();
    await user.type(screen.getByLabelText(t("recipe.fieldTitle")), "Suppe");
    await user.clear(screen.getByLabelText(t("recipe.fieldServings")));
    await user.type(screen.getByLabelText(t("recipe.fieldServings")), "101");
    await user.type(screen.getByLabelText(t("recipe.fieldPrep")), "1441");
    await pick(user, 0, "tor", "Torsk");
    await user.type(within(rows()[0]!).getByLabelText(t("recipe.quantity")), "0,00001");
    await user.click(save());
    expect(screen.getByLabelText(t("recipe.fieldServings"))).toHaveAccessibleDescription(t("recipe.error.servings", { min: 1, max: 100 }));
    expect(screen.getByLabelText(t("recipe.fieldPrep"))).toHaveAccessibleDescription(t("recipe.error.minutes", { max: 1440 }));
    expect(within(rows()[0]!).getByLabelText(t("recipe.quantity"))).toHaveAttribute("aria-invalid", "true");
    expect(server.saved).toEqual([]);
  });
});

describe("creating a recipe", () => {
  it("sends structured lines (ingredient id, quantity, unit; a to-taste line; ordered steps; tags) and opens the new recipe", async () => {
    const user = userEvent.setup();
    await formReady();
    await user.type(screen.getByLabelText(t("recipe.fieldTitle")), "  Fiskesuppe ");
    await user.type(screen.getByLabelText(t("recipe.fieldDescription")), "God");
    await user.clear(screen.getByLabelText(t("recipe.fieldServings")));
    await user.type(screen.getByLabelText(t("recipe.fieldServings")), "6");
    await user.type(screen.getByLabelText(t("recipe.fieldCook")), "30");

    await pick(user, 0, "tor", "Torsk");
    await user.type(within(rows()[0]!).getByLabelText(t("recipe.quantity")), "1,5");
    await selectUnit(user, 0, "u-g");

    await user.click(screen.getByRole("button", { name: t("recipe.addIngredient") }));
    await waitFor(() => expect(rows()).toHaveLength(2));
    await pick(user, 1, "salt", "Salt");
    await user.selectOptions(within(rows()[1]!).getByLabelText(t("recipe.amountKind")), "ToTaste");
    await user.type(within(rows()[1]!).getByLabelText(t("recipe.note")), "etter smak");

    await user.type(within(steps()[0]!).getByRole("textbox"), "Kok opp vannet");
    await user.click(screen.getByRole("button", { name: t("recipe.addStep") }));
    await user.type(within(steps()[1]!).getByRole("textbox"), "Ha i fisken");

    await user.click(screen.getByRole("button", { name: "Rask" }));
    expect(screen.getByRole("button", { name: "Rask" })).toHaveAttribute("aria-pressed", "true");

    await user.click(save());
    await waitFor(() => expect(url()).toBe("/recipes/new1"));
    expect(server.saved).toHaveLength(1);
    expect(server.saved[0]).toMatchObject({ method: "POST", path: "/api/households/h1/recipes" });
    expect(server.saved[0]!.body).toEqual({
      title: "Fiskesuppe",
      description: "God",
      servings: 6,
      prepMinutes: null,
      cookMinutes: 30,
      tagIds: ["t-quick"],
      ingredients: [
        { ingredientId: "i-torsk", quantity: 1.5, unitId: "u-g", note: null, amountKind: "Measured", purpose: "Main", isOptional: false, alternativeIds: [] },
        { ingredientId: "i-salt", quantity: null, unitId: null, note: "etter smak", amountKind: "ToTaste", purpose: "Main", isOptional: false, alternativeIds: [] },
      ],
      steps: [{ text: "Kok opp vannet" }, { text: "Ha i fisken" }],
      sourceUrl: null,
      sourceName: null,
    });
  });

  it("searches the catalog with ?search= and can create the household's own ingredient (dimension, staple)", async () => {
    const user = userEvent.setup();
    await formReady();
    await user.type(within(rows()[0]!).getByLabelText(t("recipe.ingredientSearch")), "kardemomme");
    expect(await within(rows()[0]!).findByText(t("recipe.ingredientNone"))).toBeInTheDocument();
    await user.click(within(rows()[0]!).getByRole("button", { name: t("recipe.ingredientCreate", { name: "kardemomme" }) }));
    await user.selectOptions(within(rows()[0]!).getByLabelText(t("recipe.ingredientDimension")), "Count");
    await user.click(within(rows()[0]!).getByRole("checkbox", { name: t("recipe.ingredientStaple") }));
    await user.click(within(rows()[0]!).getByRole("button", { name: t("recipe.ingredientCreateSubmit") }));
    expect(await within(rows()[0]!).findByText(t("recipe.ingredientChosen", { name: "kardemomme" }))).toBeInTheDocument();
    expect(server.createdIngredients).toEqual([{ name: "kardemomme", dimension: "Count", isStaple: true }]);
  });

  it("explains a duplicate ingredient name in the user's language", async () => {
    server.ingredientCreateStatus = 409;
    const user = userEvent.setup();
    await formReady();
    await user.type(within(rows()[0]!).getByLabelText(t("recipe.ingredientSearch")), "Tors");
    await user.click(await within(rows()[0]!).findByRole("button", { name: t("recipe.ingredientCreate", { name: "Tors" }) }));
    await user.click(within(rows()[0]!).getByRole("button", { name: t("recipe.ingredientCreateSubmit") }));
    expect(await within(rows()[0]!).findByRole("alert")).toHaveTextContent(t("recipe.problem.ingredientExists"));
  });
});

describe("ingredient and step rows", () => {
  it("moves steps up and down, removes one, and keeps the first/last move buttons inert", async () => {
    const user = userEvent.setup();
    await formReady("/recipes/r1/edit");
    expect(stepTexts()).toEqual(["Kok opp vannet", "Ha i fisken"]);

    await user.click(screen.getByRole("button", { name: t("recipe.moveStepDown", { n: 1 }) }));
    expect(stepTexts()).toEqual(["Ha i fisken", "Kok opp vannet"]);
    // Labels follow the position, and the button at the end of the list does nothing.
    expect(screen.getByRole("button", { name: t("recipe.moveStepUp", { n: 1 }) })).toHaveAttribute("aria-disabled", "true");
    await user.click(screen.getByRole("button", { name: t("recipe.moveStepUp", { n: 1 }) }));
    expect(stepTexts()).toEqual(["Ha i fisken", "Kok opp vannet"]);

    await user.click(screen.getByRole("button", { name: t("recipe.moveStepUp", { n: 2 }) }));
    expect(stepTexts()).toEqual(["Kok opp vannet", "Ha i fisken"]);

    await user.click(screen.getByRole("button", { name: t("recipe.removeStep", { n: 1 }) }));
    expect(stepTexts()).toEqual(["Ha i fisken"]);
  });

  it("adds, reorders and removes ingredient lines, a to-taste line keeping its note", async () => {
    const user = userEvent.setup();
    await formReady("/recipes/r1/edit");
    const names = () => rows().map((r) => within(r).getByText(/^(Torsk|Salt|Melk)|: (Torsk|Salt|Melk)$/).textContent);
    expect(rows()).toHaveLength(2);
    expect(within(rows()[1]!).getByLabelText(t("recipe.amountKind"))).toHaveValue("ToTaste");
    expect(within(rows()[1]!).getByLabelText(t("recipe.note"))).toHaveValue("etter smak");
    expect(names()).toEqual([t("recipe.ingredientChosen", { name: "Torsk" }), t("recipe.ingredientChosen", { name: "Salt" })]);

    await user.click(screen.getByRole("button", { name: t("recipe.moveIngredientUp", { n: 2 }) }));
    expect(names()).toEqual([t("recipe.ingredientChosen", { name: "Salt" }), t("recipe.ingredientChosen", { name: "Torsk" })]);
    expect(within(rows()[0]!).getByLabelText(t("recipe.amountKind"))).toHaveValue("ToTaste");
    expect(within(rows()[1]!).getByLabelText(t("recipe.quantity"))).toHaveValue("400");

    await user.click(screen.getByRole("button", { name: t("recipe.addIngredient") }));
    expect(rows()).toHaveLength(3);
    await waitFor(() => expect(within(rows()[2]!).getByLabelText(t("recipe.ingredientSearch"))).toHaveFocus());

    await user.click(screen.getByRole("button", { name: t("recipe.removeIngredient", { n: 3 }) }));
    await user.click(screen.getByRole("button", { name: t("recipe.removeIngredient", { n: 1 }) }));
    expect(rows()).toHaveLength(1);
    expect(within(rows()[0]!).getByText(t("recipe.ingredientChosen", { name: "Torsk" }))).toBeInTheDocument();
  });
});

describe("editing a recipe", () => {
  it("fills the form from the recipe and saves with PUT, in the lines' new order", async () => {
    const user = userEvent.setup();
    await formReady("/recipes/r1/edit");
    expect(screen.getByLabelText(t("recipe.fieldTitle"))).toHaveValue("Fiskesuppe");
    expect(screen.getByLabelText(t("recipe.fieldServings"))).toHaveValue("4");
    expect(screen.getByRole("button", { name: "Middag" })).toHaveAttribute("aria-pressed", "true");

    await user.click(screen.getByRole("button", { name: t("recipe.moveStepDown", { n: 1 }) }));
    await user.clear(screen.getByLabelText(t("recipe.fieldServings")));
    await user.type(screen.getByLabelText(t("recipe.fieldServings")), "8");
    await user.click(save());

    await waitFor(() => expect(url()).toBe("/recipes/r1"));
    expect(server.saved).toHaveLength(1);
    expect(server.saved[0]).toMatchObject({ method: "PUT", path: "/api/households/h1/recipes/r1" });
    expect(server.saved[0]!.body).toMatchObject({
      servings: 8,
      steps: [{ text: "Ha i fisken" }, { text: "Kok opp vannet" }],
      tagIds: ["t-dinner"],
    });
  });

  it("says so when the recipe does not exist", async () => {
    renderAt("/recipes/nope/edit");
    expect(await screen.findByText(t("recipes.notFound"))).toBeInTheDocument();
  });
});

describe("server validation errors land on the right field", () => {
  const problem = (errors: Record<string, string[]>) => ({
    type: "https://tools.ietf.org/html/rfc9110#section-15.5.1",
    title: "One or more validation errors occurred.",
    status: 400,
    errors,
  });

  it("maps ingredients[i].unitId, ingredients[i].ingredientId and tagIds[i] (ids the client could not know were foreign)", async () => {
    server.saveStatus = 400;
    server.saveBody = problem({
      "ingredients[1].unitId": ["The unit does not exist."],
      "ingredients[0].ingredientId": ["The ingredient does not exist."],
      "tagIds[0]": ["The tag does not exist."],
    });
    const user = userEvent.setup();
    await formReady("/recipes/r1/edit");
    await user.click(screen.getByRole("button", { name: "Rask" })); // a second tag, so tagIds has two entries
    await user.click(save());

    const summary = await screen.findByTestId("error-summary");
    expect(summary).toHaveFocus();
    expect(within(summary).getByText(t("recipe.errorSummary", { count: 3 }))).toBeInTheDocument();
    expect(server.saved).toHaveLength(1); // the client let it through

    expect(within(rows()[0]!).getByText(t("recipe.error.ingredientUnknown"))).toBeInTheDocument();
    // Line 2 is the to-taste line, so a unit there is "not allowed".
    expect(within(rows()[1]!).queryByText(t("recipe.error.unitNotAllowed"))).toBeNull(); // hidden with the unit select
    expect(within(summary).getByText(new RegExp(t("recipe.error.unitNotAllowed")))).toBeInTheDocument();
    expect(screen.getByText(t("recipe.error.tagUnknown", {}), { selector: ".field-error" })).toBeInTheDocument();
    // Never the server's English text.
    expect(screen.queryByText(/does not exist/)).toBeNull();
  });

  it("shows the unit error on the unit select when the line has an amount", async () => {
    server.saveStatus = 400;
    server.saveBody = problem({ "ingredients[0].unitId": ["The unit does not exist."], title: ["x"], "steps[1].text": ["x"] });
    const user = userEvent.setup();
    await formReady("/recipes/r1/edit");
    await user.click(save());
    await screen.findByTestId("error-summary");
    const unit = within(rows()[0]!).getByLabelText(t("recipe.unit"));
    expect(unit).toHaveAttribute("aria-invalid", "true");
    expect(unit).toHaveAccessibleDescription(t("recipe.error.unitUnknown"));
    expect(screen.getByLabelText(t("recipe.fieldTitle"))).toHaveAccessibleDescription(t("recipe.error.title", { max: 200 }));
    expect(within(steps()[1]!).getByText(t("recipe.error.step", { max: 2000 }))).toBeInTheDocument();
  });

  it("shows a translated message for other failures (403 forbidden), never the server text", async () => {
    server.saveStatus = 403;
    server.saveBody = { type: urn("forbidden"), status: 403 };
    const user = userEvent.setup();
    await formReady("/recipes/r1/edit");
    await user.click(save());
    const summary = await screen.findByTestId("error-summary");
    expect(summary).toHaveTextContent(t("recipe.saveFailed"));
    expect(summary).toHaveTextContent(t("problem.forbidden"));
  });
});

describe("picture", () => {
  const png = () => new File([new Uint8Array([137, 80, 78, 71])], "soup.png", { type: "image/png" });

  beforeEach(() => {
    URL.createObjectURL = vi.fn(() => "blob:preview");
    URL.revokeObjectURL = vi.fn();
  });

  it("previews the chosen file, and uploads it as the multipart part `file` after the recipe is saved", async () => {
    const calls: { path: unknown; file: unknown }[] = [];
    const original = api.PUT.bind(api) as (...args: unknown[]) => Promise<unknown>;
    vi.spyOn(api, "PUT").mockImplementation((async (path: string, init: { bodySerializer?: (b: unknown) => FormData; body?: unknown }) => {
      if (path.endsWith("/image")) {
        calls.push({ path, file: init.bodySerializer?.(init.body)?.get("file") });
        return { data: {}, response: new Response("{}", { status: 200 }) };
      }
      return original(path, init);
    }) as never);

    const user = userEvent.setup();
    await formReady("/recipes/r1/edit");
    expect(screen.getByText(t("recipe.pictureNone"))).toBeInTheDocument();
    const file = png();
    await user.upload(screen.getByLabelText(t("recipe.pictureFile")), file);
    expect(screen.getByTestId("picture-preview")).toHaveAttribute("src", "blob:preview");
    expect(screen.getByText(t("recipe.pictureNew"))).toBeInTheDocument();

    await user.click(save());
    await waitFor(() => expect(url()).toBe("/recipes/r1"));
    expect(calls).toHaveLength(1);
    expect(calls[0]!.file).toBeInstanceOf(File);
    expect((calls[0]!.file as File).name).toBe("soup.png");
    expect(String(calls[0]!.path)).toBe("/api/households/{householdId}/recipes/{recipeId}/image");
  });

  it("can drop the chosen file again before saving, so nothing is uploaded", async () => {
    const spy = vi.spyOn(api, "PUT");
    const user = userEvent.setup();
    await formReady("/recipes/r1/edit");
    await user.upload(screen.getByLabelText(t("recipe.pictureFile")), png());
    await user.click(screen.getByRole("button", { name: t("recipe.pictureRemove") }));
    expect(screen.queryByTestId("picture-preview")).toBeNull();
    await user.click(save());
    await waitFor(() => expect(url()).toBe("/recipes/r1"));
    expect((spy.mock.calls as unknown[][]).filter((call) => String(call[0]).endsWith("/image"))).toHaveLength(0);
  });

  it("removes the stored picture with DELETE on save, and 'keep' undoes that", async () => {
    server.recipe = existing({ image: { thumbUrl: "/i/t", cardUrl: "/i/c", fullUrl: "/i/f" } });
    const user = userEvent.setup();
    await formReady("/recipes/r1/edit");
    await user.click(screen.getByRole("button", { name: t("recipe.pictureRemove") }));
    expect(screen.getByText(t("recipe.pictureRemoving"))).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: t("recipe.pictureUndo") }));
    expect(screen.queryByText(t("recipe.pictureRemoving"))).toBeNull();
    await user.click(screen.getByRole("button", { name: t("recipe.pictureRemove") }));
    await user.click(save());
    await waitFor(() => expect(url()).toBe("/recipes/r1"));
    expect(server.calls).toContain("DELETE /api/households/h1/recipes/r1/image");
  });

  it("blocks a file of the wrong type or size before any request", async () => {
    const user = userEvent.setup({ applyAccept: false });
    await formReady("/recipes/r1/edit");
    await user.upload(screen.getByLabelText(t("recipe.pictureFile")), new File(["x"], "a.gif", { type: "image/gif" }));
    expect(screen.getByLabelText(t("recipe.pictureFile"))).toHaveAccessibleDescription(t("recipe.error.imageType"));
    const big = new File(["x"], "big.png", { type: "image/png" });
    Object.defineProperty(big, "size", { value: 11 * 1024 * 1024 });
    await user.upload(screen.getByLabelText(t("recipe.pictureFile")), big);
    expect(screen.getByLabelText(t("recipe.pictureFile"))).toHaveAccessibleDescription(t("recipe.error.imageSize", { max: 10 }));
    expect(screen.queryByTestId("picture-preview")).toBeNull();
  });

  it("keeps the saved recipe and shows the picture error when the upload is refused", async () => {
    vi.spyOn(api, "PUT").mockImplementation((async (path: string) => {
      if (path.endsWith("/image")) return { error: {}, response: new Response("{}", { status: 400 }) };
      return { data: { ...existing(), id: "new1" }, response: new Response("{}", { status: 201 }) };
    }) as never);
    const user = userEvent.setup();
    await formReady("/recipes/r1/edit");
    await user.upload(screen.getByLabelText(t("recipe.pictureFile")), png());
    await user.click(save());
    const file = screen.getByLabelText(t("recipe.pictureFile"));
    await waitFor(() => expect(file).toHaveAccessibleDescription(t("recipe.error.imageInvalid")));
    expect(url()).toBe("/recipes/r1/edit");
  });

  it("shows the translated 'picture too large' message for a bare nginx 413 (HTML body, no ProblemDetails) (T-080)", async () => {
    vi.spyOn(api, "PUT").mockImplementation((async (path: string) => {
      if (path.endsWith("/image")) {
        return { error: "<html>413 Request Entity Too Large</html>", response: new Response("<html></html>", { status: 413 }) };
      }
      return { data: { ...existing(), id: "new1" }, response: new Response("{}", { status: 201 }) };
    }) as never);
    const user = userEvent.setup();
    await formReady("/recipes/r1/edit");
    await user.upload(screen.getByLabelText(t("recipe.pictureFile")), png());
    await user.click(save());
    const file = screen.getByLabelText(t("recipe.pictureFile"));
    await waitFor(() => expect(file).toHaveAccessibleDescription(t("recipe.error.imageSize", { max: 10 })));
    expect(screen.queryByText(/\{max\}/)).toBeNull();
  });
});

describe("delete", () => {
  it("asks first, and cancel changes nothing", async () => {
    const user = userEvent.setup();
    await formReady("/recipes/r1/edit");
    await user.click(screen.getByRole("button", { name: t("recipe.delete") }));
    expect(screen.getByText(t("recipe.deleteConfirm", { title: "Fiskesuppe" }))).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: t("recipe.cancel") }));
    expect(screen.queryByText(t("recipe.deleteConfirm", { title: "Fiskesuppe" }))).toBeNull();
    expect(server.calls.some((c) => c.startsWith("DELETE"))).toBe(false);
  });

  it("explains 409 recipe-in-use in the user's language and stays on the page", async () => {
    server.deleteStatus = 409;
    server.deleteType = urn("recipe-in-use");
    const user = userEvent.setup();
    await formReady("/recipes/r1/edit");
    await user.click(screen.getByRole("button", { name: t("recipe.delete") }));
    await user.click(screen.getByRole("button", { name: t("recipe.deleteYes") }));
    expect(await screen.findByRole("alert")).toHaveTextContent(t("recipe.problem.inUse"));
    expect(url()).toBe("/recipes/r1/edit");
    expect(screen.getByRole("button", { name: t("recipe.deleteYes") })).toBeEnabled();
  });

  it("deletes and returns to the grid", async () => {
    const user = userEvent.setup();
    await formReady("/recipes/r1/edit");
    await user.click(screen.getByRole("button", { name: t("recipe.delete") }));
    await user.click(screen.getByRole("button", { name: t("recipe.deleteYes") }));
    await waitFor(() => expect(url()).toBe("/recipes"));
    expect(server.calls).toContain("DELETE /api/households/h1/recipes/r1");
  });
});

describe("own tag", () => {
  it("creates a tag inline and selects it", async () => {
    vi.spyOn(api, "POST").mockImplementation((async (path: string, init: { body: { name: string } }) => {
      expect(path).toBe("/api/households/{householdId}/tags");
      return { data: { id: "t-new", name: init.body.name, kind: "Tag", isGlobal: false }, response: new Response("{}", { status: 201 }) };
    }) as never);
    const user = userEvent.setup();
    await formReady();
    await user.type(screen.getByLabelText(t("recipe.newTag")), "Helg");
    await user.click(screen.getByRole("button", { name: t("recipe.addTag") }));
    expect(await screen.findByRole("button", { name: "Helg" })).toHaveAttribute("aria-pressed", "true");
  });
});

describe("recipe detail", () => {
  const detailWith = (patch: Partial<Recipe> = {}) => {
    server.recipe = existing(patch);
  };

  it("shows times, tags, ingredients with quantities and units, and steps", async () => {
    detailWith({ image: { thumbUrl: "/i/t", cardUrl: "/i/c", fullUrl: "/i/f" } });
    const { container } = renderAt("/recipes/r1");
    expect(await screen.findByRole("heading", { level: 1, name: "Fiskesuppe" })).toBeInTheDocument();
    expect(screen.getByText(t("recipes.minutes", { minutes: 10 }))).toBeInTheDocument();
    expect(screen.getByText(t("recipes.minutes", { minutes: 30 }))).toBeInTheDocument();
    expect(screen.getByText(t("recipes.minutes", { minutes: 40 }))).toBeInTheDocument();
    expect(screen.getByText("Middag")).toBeInTheDocument();
    const items = screen.getAllByRole("listitem").map((li) => li.textContent);
    expect(items.some((x) => x?.includes("400 g") && x.includes("Torsk"))).toBe(true);
    expect(items.some((x) => x?.includes("Salt (etter smak)"))).toBe(true);
    expect(screen.getByText("Ha i fisken")).toBeInTheDocument();
    const img = container.querySelector("img")!;
    expect(img).toHaveAttribute("src", "/i/c");
    expect(img.getAttribute("srcset")).toContain("/i/f 1600w");
  });

  it("shows the quantities the API scaled for the chosen servings without saving anything, and resets", async () => {
    const user = userEvent.setup();
    renderAt("/recipes/r1");
    await screen.findByRole("heading", { level: 1, name: "Fiskesuppe" });
    const amounts = () => screen.getAllByRole("listitem").map((li) => li.textContent ?? "");
    const more = screen.getByRole("button", { name: t("recipe.servingsMore") });
    for (let i = 0; i < 4; i++) await user.click(more); // 4 -> 8 servings: double
    expect(screen.getByTestId("servings-value")).toHaveTextContent("8");
    await waitFor(() => expect(amounts().some((x) => x.includes("800 g") && x.includes("Torsk"))).toBe(true));
    expect(amounts().some((x) => x.includes("Salt (etter smak)"))).toBe(true); // to taste stays as is
    expect(screen.getByText(t("recipe.servingsScaled", { servings: 4 }))).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: t("recipe.servingsFewer") }));
    await waitFor(() => expect(amounts().some((x) => x.includes("700 g"))).toBe(true));
    await user.click(screen.getByRole("button", { name: t("recipe.servingsReset", { servings: 4 }) }));
    expect(amounts().some((x) => x.includes("400 g"))).toBe(true);
    // One request per servings change, none at the recipe's own servings: the client does no scaling math.
    expect(server.scaledCalls).toEqual(["5", "6", "7", "8", "7"]);
    expect(server.calls.some((c) => c.startsWith("PUT") || c.startsWith("POST /api/households/h1/recipes"))).toBe(false);
  });
});

describe("recipe source: website or book/other (T-081)", () => {
  it("shows a website as the link, and a book or other source as plain text that is never a link", async () => {
    server.recipe = existing({ sourceUrl: "https://www.matprat.no/x", sourceName: "matprat.no" });
    const web = renderAt("/recipes/r1");
    const link = await screen.findByTestId("recipe-source-link");
    expect(link).toHaveAttribute("href", "https://www.matprat.no/x");
    expect(link).toHaveTextContent(t("recipe.sourceOriginal", { name: "matprat.no" }));
    expect(screen.queryByTestId("recipe-source-text")).toBeNull();
    web.unmount();

    server.recipe = existing({ sourceUrl: null, sourceName: "Kaptein Sabeltann, Terje Formoe" });
    const book = renderAt("/recipes/r1");
    const text = await screen.findByTestId("recipe-source-text");
    expect(text).toHaveTextContent(t("recipe.sourceText", { name: "Kaptein Sabeltann, Terje Formoe" }));
    expect(screen.queryByTestId("recipe-source-link")).toBeNull();
    expect(text.querySelector("a")).toBeNull();
    book.unmount();

    // A stored URL that is not http(s) is never a link; with a name it falls back to text.
    server.recipe = existing({ sourceUrl: "javascript:alert(1)", sourceName: "Fusk" });
    renderAt("/recipes/r1");
    expect(await screen.findByTestId("recipe-source-text")).toHaveTextContent(t("recipe.sourceText", { name: "Fusk" }));
    expect(document.querySelector("a[href^='javascript']")).toBeNull();
  });

  it("sends a book as a name only, and switching type clears the other type's fields", async () => {
    const user = userEvent.setup();
    server.recipe = existing({ sourceUrl: "https://www.matprat.no/x", sourceName: "matprat.no" });
    await formReady("/recipes/r1/edit");
    expect(screen.getByTestId("source-kind-website")).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByLabelText(t("recipe.fieldSource"))).toHaveValue("https://www.matprat.no/x");
    expect(screen.getByLabelText(t("recipe.fieldSourceName"))).toHaveValue("matprat.no");

    await user.click(screen.getByTestId("source-kind-book"));
    expect(screen.getByTestId("source-kind-book")).toHaveAttribute("aria-pressed", "true");
    expect(screen.queryByLabelText(t("recipe.fieldSource"))).toBeNull();
    expect(screen.getByLabelText(t("recipe.fieldSourceBook"))).toHaveValue("");
    await user.type(screen.getByLabelText(t("recipe.fieldSourceBook")), "Foo");

    await user.click(screen.getByTestId("source-kind-website"));
    expect(screen.getByLabelText(t("recipe.fieldSource"))).toHaveValue("");
    expect(screen.getByLabelText(t("recipe.fieldSourceName"))).toHaveValue("");
    await user.click(screen.getByTestId("source-kind-book"));
    expect(screen.getByLabelText(t("recipe.fieldSourceBook"))).toHaveValue("");
    await user.type(screen.getByLabelText(t("recipe.fieldSourceBook")), "Matskatter, s. 12");

    await user.click(save());
    await waitFor(() => expect(server.saved).toHaveLength(1));
    expect(server.saved[0]!.body).toMatchObject({ sourceUrl: null, sourceName: "Matskatter, s. 12" });
  });

  it("opens an existing book source in book mode and refuses a too long name", async () => {
    const user = userEvent.setup();
    server.recipe = existing({ sourceUrl: null, sourceName: "Min kokebok" });
    await formReady("/recipes/r1/edit");
    expect(screen.getByTestId("source-kind-book")).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByLabelText(t("recipe.fieldSourceBook"))).toHaveValue("Min kokebok");
    await user.type(screen.getByLabelText(t("recipe.fieldSourceBook")), "x".repeat(100));
    await user.click(save());
    expect(await screen.findAllByText(t("recipe.error.sourceName", { max: 100 }))).not.toHaveLength(0);
    expect(server.saved).toEqual([]);
  });

  it("refuses a javascript: link on a website and sends a link with its name", async () => {
    const user = userEvent.setup();
    await formReady("/recipes/r1/edit");
    await user.type(screen.getByLabelText(t("recipe.fieldSource")), "javascript:alert(1)");
    await user.click(save());
    expect(screen.getByLabelText(t("recipe.fieldSource"))).toHaveAttribute("aria-invalid", "true");
    expect(server.saved).toEqual([]);

    await user.clear(screen.getByLabelText(t("recipe.fieldSource")));
    await user.type(screen.getByLabelText(t("recipe.fieldSource")), "https://example.com/r");
    await user.type(screen.getByLabelText(t("recipe.fieldSourceName")), "Mormors blogg");
    await user.click(save());
    await waitFor(() => expect(server.saved).toHaveLength(1));
    expect(server.saved[0]!.body).toMatchObject({ sourceUrl: "https://example.com/r", sourceName: "Mormors blogg" });
  });
});

describe("amount kind, purpose, optional and alternatives (T-076)", () => {
  it("sets them on a line, picks an alternative with the ingredient picker and sends them", async () => {
    const user = userEvent.setup();
    await formReady();
    await user.type(screen.getByLabelText(t("recipe.fieldTitle")), "Stek");
    await pick(user, 0, "melk", "Melk");
    await user.selectOptions(within(rows()[0]!).getByLabelText(t("recipe.amountKind")), "AsNeeded");
    expect(within(rows()[0]!).queryByLabelText(t("recipe.quantity"))).toBeNull(); // no amount for "as needed"
    await user.selectOptions(within(rows()[0]!).getByLabelText(t("recipe.purpose")), "Frying");
    await user.click(within(rows()[0]!).getByRole("checkbox", { name: t("recipe.optional") }));

    const alternatives = within(rows()[0]!).getByTestId("alternatives");
    await user.click(within(alternatives).getByRole("button", { name: t("recipe.alternativeAdd") }));
    await user.type(within(alternatives).getByLabelText(t("recipe.ingredientSearch")), "tor");
    await user.click(await within(alternatives).findByRole("button", { name: "Torsk" }, { timeout: searchDelayMs + 2000 }));
    expect(within(alternatives).getByText("Torsk")).toBeInTheDocument();

    await user.type(within(steps()[0]!).getByRole("textbox"), "Stek");
    await user.click(save());
    await waitFor(() => expect(server.saved).toHaveLength(1));
    expect(server.saved[0]!.body.ingredients).toEqual([
      {
        ingredientId: "i-melk", quantity: null, unitId: null, note: null,
        amountKind: "AsNeeded", purpose: "Frying", isOptional: true, alternativeIds: ["i-torsk"],
      },
    ]);
  });

  it("removes an alternative and cannot add the line's own ingredient", async () => {
    const user = userEvent.setup();
    await formReady();
    await pick(user, 0, "melk", "Melk");
    const alternatives = within(rows()[0]!).getByTestId("alternatives");
    await user.click(within(alternatives).getByRole("button", { name: t("recipe.alternativeAdd") }));
    await user.type(within(alternatives).getByLabelText(t("recipe.ingredientSearch")), "melk");
    await user.click(await within(alternatives).findByRole("button", { name: "Melk" }, { timeout: searchDelayMs + 2000 }));
    expect(within(alternatives).queryByRole("button", { name: t("recipe.alternativeRemove", { name: "Melk" }) })).toBeNull();

    await user.click(within(alternatives).getByRole("button", { name: t("recipe.alternativeAdd") }));
    await user.type(within(alternatives).getByLabelText(t("recipe.ingredientSearch")), "tor");
    await user.click(await within(alternatives).findByRole("button", { name: "Torsk" }, { timeout: searchDelayMs + 2000 }));
    await user.click(within(alternatives).getByRole("button", { name: t("recipe.alternativeRemove", { name: "Torsk" }) }));
    expect(within(alternatives).queryByText("Torsk")).toBeNull();
  });

  it("edits an existing recipe's kinds and alternatives", async () => {
    server.recipe = existing({
      ingredients: [
        {
          ingredientId: "i-salt", ingredientName: "Salt", quantity: null, unitId: null, unitCode: null, unitName: null, note: null,
          amountKind: "ToTaste", purpose: "Seasoning", isOptional: true, alternatives: [{ ingredientId: "i-melk", ingredientName: "Melk" }],
        },
      ],
    });
    renderAt("/recipes/r1/edit");
    const row = (await screen.findAllByTestId("ingredient-row"))[0]!;
    expect(within(row).getByLabelText(t("recipe.amountKind"))).toHaveValue("ToTaste");
    expect(within(row).getByLabelText(t("recipe.purpose"))).toHaveValue("Seasoning");
    expect(within(row).getByRole("checkbox", { name: t("recipe.optional") })).toBeChecked();
    expect(within(row).getByRole("button", { name: t("recipe.alternativeRemove", { name: "Melk" }) })).toBeInTheDocument();
  });
});
