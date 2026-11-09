import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAntiforgeryToken } from "../api/client";
import type { components } from "../api/schema";
import { registerMessages } from "../i18n";
import { nb } from "../i18n/nb";
import { suggestNb } from "../i18n/suggest-nb";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import WhatCanIMake, { groupSuggestions } from "./what-can-i-make";

type Item = components["schemas"]["RecipeSuggestionResponse"];
type Missing = components["schemas"]["MissingIngredientResponse"];
type Role = components["schemas"]["HouseholdRole"];

const me = { userId: "11111111-1111-1111-1111-111111111111", email: "owner@example.test" };
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });
const problem = (type: string, status: number) => json({ type: `urn:foodplanner:problem:${type}`, title: "x", status }, status);

const missing = (over: Partial<Missing> & { name: string }): Missing => ({
  ingredientId: `i-${over.name}`,
  quantities: [],
  reason: "NotAtHome",
  alternatives: [],
  ...over,
});

const item = (over: Partial<Item> & { recipeId: string; title: string }): Item => ({
  servings: 4,
  totalMinutes: 30,
  foodType: "Vegetarian",
  thumbUrl: null,
  cardUrl: null,
  kidLikes: 0,
  cookedRecently: false,
  plannedToday: false,
  missing: [],
  ...over,
});

type Call = { method: string; path: string; query: string; body: unknown };
let role: Role;
let items: Item[];
let pantryCount: number;
let calls: Call[];
let cookStatus: Response | null;
let addStatus: Response | null;
let listStatus: number;
let kidMode: unknown = null;

beforeEach(() => {
  resetAntiforgeryToken();
  role = "Owner";
  calls = [];
  cookStatus = null;
  addStatus = null;
  listStatus = 200;
  kidMode = null;
  pantryCount = 3;
  items = [
    item({ recipeId: "r-pasta", title: "Pasta", kidLikes: 2, cookedRecently: true }),
    item({ recipeId: "r-soup", title: "Soup", foodType: "Unknown" }),
    item({ recipeId: "r-taco", title: "Tacos", missing: [missing({ name: "Cheese", quantities: [{ amount: 200, unitCode: "g", unitName: "Gram" }] })] }),
    item({
      recipeId: "r-curry",
      title: "Curry",
      missing: [
        missing({ name: "Rice", reason: "NotEnough", quantities: [{ amount: 100, unitCode: "g", unitName: "Gram" }] }),
        missing({ name: "Garlic", reason: "UnitMismatch", quantities: [{ amount: 3, unitCode: "fedd", unitName: "Fedd" }] }),
        missing({ name: "Milk", alternatives: [{ ingredientId: "i-cream", ingredientName: "Cream" }] }),
      ],
    }),
  ];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const url = new URL(request.url);
      const path = url.pathname;
      const method = request.method;
      if (path === "/api/auth/me") return json({ ...me, kidMode });
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/households") return json([{ id: "h1", name: "Home", role }]);
      const body = method === "GET" ? null : await request.json().catch(() => null);
      calls.push({ method, path, query: url.search, body });
      if (path === "/api/households/h1/what-can-i-make") return listStatus === 200 ? json({ today: "2026-11-09", weekStart: "2026-11-09", items }) : problem("kid-mode", listStatus);
      if (path === "/api/households/h1/pantry") return json(Array.from({ length: pantryCount }, (_, i) => ({ ingredientId: `p${i}` })));
      const cook = path.match(/what-can-i-make\/([^/]+)\/cook-today$/);
      if (cook) {
        if (cookStatus) return cookStatus;
        const entry = { id: "e1" };
        return json({ entry, weekStart: "2026-11-09", alreadyPlanned: false }, 201);
      }
      const add = path.match(/what-can-i-make\/([^/]+)\/add-missing$/);
      if (add) {
        if (addStatus) return addStatus;
        return json({ weekStart: "2026-11-09", missing: items.find((i) => i.recipeId === add[1])!.missing });
      }
      throw new Error(`unexpected ${method} ${path}`);
    }),
  );
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function renderPage() {
  return render(
    <MemoryRouter initialEntries={["/what-can-i-make"]}>
      <SessionProvider>
        <Routes>
          <Route path="/what-can-i-make" element={<WhatCanIMake />} />
          <Route path="/login" element={<p>login page</p>} />
          <Route path="/" element={<p>home page</p>} />
          <Route path="*" element={<p>elsewhere</p>} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

const card = (title: string) => screen.getAllByTestId("suggest-card").find((c) => c.textContent?.includes(title))!;

describe("groupSuggestions", () => {
  it("splits ready from almost and keeps the API order in both", () => {
    const { ready, almost } = groupSuggestions(items);
    expect(ready.map((i) => i.title)).toEqual(["Pasta", "Soup"]);
    expect(almost.map((i) => i.title)).toEqual(["Tacos", "Curry"]);
  });
});

describe("what can I make", () => {
  it("shows ready recipes first, then those missing things, each in API order", async () => {
    renderPage();
    const ready = await screen.findByTestId("suggest-ready");
    const almost = screen.getByTestId("suggest-almost");
    expect(within(ready).getByRole("heading", { name: t("suggest.ready.title") })).toBeInTheDocument();
    expect(within(almost).getByRole("heading", { name: t("suggest.missing.title") })).toBeInTheDocument();
    expect(ready.compareDocumentPosition(almost) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(within(ready).getAllByTestId("suggest-card").map((c) => c.querySelector(".recipe-card-title")!.textContent)).toEqual(["Pasta", "Soup"]);
    expect(within(almost).getAllByTestId("suggest-card").map((c) => c.querySelector(".recipe-card-title")!.textContent)).toEqual(["Tacos", "Curry"]);
  });

  it("writes missing lines: an amount, 'more' for a shortfall, just the name for a unit mismatch, alternatives as 'or'", async () => {
    renderPage();
    await screen.findByTestId("suggest-almost");
    const tacos = within(card("Tacos")).getByTestId("suggest-missing");
    expect(tacos).toHaveTextContent(t("suggest.missingLine", { item: t("suggest.line.amount", { amount: "200 g", name: "Cheese" }) }));
    const lines = within(card("Curry")).getAllByRole("listitem").map((l) => l.textContent);
    expect(lines).toEqual([
      t("suggest.missingLine", { item: t("suggest.line.more", { amount: "100 g", name: "Rice" }) }),
      t("suggest.missingLine", { item: "Garlic" }),
      t("suggest.missingLine", { item: t("suggest.line.alternatives", { item: "Milk", alternatives: "Cream" }) }),
    ]);
    expect(lines[1]).not.toMatch(/fedd|3/);
  });

  it("shows time, food type, kid likes, the cooked-recently hint and a link to the recipe", async () => {
    renderPage();
    await screen.findByTestId("suggest-ready");
    const pasta = card("Pasta");
    expect(pasta).toHaveTextContent(t("recipes.minutes", { minutes: 30 }));
    expect(within(pasta).getByTestId("suggest-food-type")).toHaveTextContent(t("foodType.Vegetarian"));
    expect(within(pasta).getByTestId("suggest-likes")).toHaveTextContent(t("suggest.likes", { count: 2 }));
    expect(within(pasta).getByTestId("suggest-cooked-recently")).toBeInTheDocument();
    expect(within(pasta).getAllByRole("link")[0]).toHaveAttribute("href", "/recipes/r-pasta");
    const soup = card("Soup");
    expect(within(soup).queryByTestId("suggest-food-type")).toBeNull();
    expect(within(soup).queryByTestId("suggest-likes")).toBeNull();
    expect(within(soup).queryByTestId("suggest-cooked-recently")).toBeNull();
  });

  it("badges a recipe planned today and offers no cook button for it", async () => {
    items[0] = { ...items[0]!, plannedToday: true };
    renderPage();
    await screen.findByTestId("suggest-ready");
    expect(within(card("Pasta")).getByTestId("suggest-planned-today")).toHaveTextContent(t("suggest.plannedToday"));
    expect(within(card("Pasta")).queryByRole("button")).toBeNull();
  });

  it("asks for the chosen servings and sends them with cook today", async () => {
    const user = userEvent.setup();
    renderPage();
    await screen.findByTestId("suggest-ready");
    expect(calls.find((c) => c.path.endsWith("what-can-i-make"))!.query).toBe("");
    await user.selectOptions(screen.getByLabelText(t("suggest.servingsLabel")), "6");
    await waitFor(() => expect(calls.some((c) => c.query === "?servings=6")).toBe(true));
    await screen.findByTestId("suggest-ready");
    await user.click(within(card("Pasta")).getByRole("button", { name: t("suggest.cookTodayFor", { title: "Pasta" }) }));
    await waitFor(() => expect(calls.find((c) => c.path.endsWith("cook-today"))).toMatchObject({ body: { servings: 6 } }));
  });

  it("cook today, then add missing: shows the plan state, the badge, then what was added without claiming it is the whole list", async () => {
    const user = userEvent.setup();
    renderPage();
    await screen.findByTestId("suggest-almost");
    const tacos = () => card("Tacos");
    expect(within(tacos()).queryByRole("button", { name: t("suggest.addMissingFor", { title: "Tacos" }) })).toBeNull();

    await user.click(within(tacos()).getByRole("button", { name: t("suggest.cookTodayFor", { title: "Tacos" }) }));
    expect(await within(tacos()).findByTestId("suggest-planned-note")).toHaveTextContent(t("suggest.planned"));
    expect(within(tacos()).getByTestId("suggest-planned-today")).toBeInTheDocument();
    expect(within(tacos()).queryByRole("button", { name: t("suggest.cookTodayFor", { title: "Tacos" }) })).toBeNull();
    expect(calls.find((c) => c.path.endsWith("r-taco/cook-today"))).toMatchObject({ method: "POST" });

    await user.click(within(tacos()).getByRole("button", { name: t("suggest.addMissingFor", { title: "Tacos" }) }));
    const note = await within(tacos()).findByTestId("suggest-added-note");
    expect(note).toHaveTextContent(t("suggest.added", { items: t("suggest.line.amount", { amount: "200 g", name: "Cheese" }) }));
    expect(note).toHaveTextContent(t("suggest.addedNote"));
    expect(within(note).getByRole("link", { name: t("suggest.openShopping") })).toHaveAttribute("href", "/shopping");
    expect(calls.some((c) => c.path.endsWith("r-taco/add-missing"))).toBe(true);
    // the list did not jump: order unchanged
    expect(screen.getAllByTestId("suggest-card")).toHaveLength(4);
  });

  it("a ready recipe planned today has no add-missing button", async () => {
    const user = userEvent.setup();
    renderPage();
    await screen.findByTestId("suggest-ready");
    await user.click(within(card("Soup")).getByRole("button", { name: t("suggest.cookTodayFor", { title: "Soup" }) }));
    await within(card("Soup")).findByTestId("suggest-planned-note");
    expect(within(card("Soup")).queryByRole("button")).toBeNull();
  });

  it("says when the recipe was already on today's plan", async () => {
    const user = userEvent.setup();
    cookStatus = json({ entry: { id: "e1" }, weekStart: "2026-11-09", alreadyPlanned: true }, 200);
    renderPage();
    await screen.findByTestId("suggest-ready");
    await user.click(within(card("Soup")).getByRole("button", { name: t("suggest.cookTodayFor", { title: "Soup" }) }));
    expect(await within(card("Soup")).findByTestId("suggest-planned-note")).toHaveTextContent(t("suggest.alreadyPlanned"));
  });

  it("hides the buttons for a Child but still lists the recipes", async () => {
    role = "Child";
    renderPage();
    await screen.findByTestId("suggest-ready");
    expect(screen.getAllByTestId("suggest-card")).toHaveLength(4);
    expect(screen.queryByRole("button", { name: /./ })).toBeNull();
    expect(screen.getByLabelText(t("suggest.servingsLabel"))).toBeInTheDocument();
  });

  it("shows week-locked, list-conflict and not-planned problems on the card", async () => {
    const user = userEvent.setup();
    cookStatus = problem("week-locked", 409);
    renderPage();
    await screen.findByTestId("suggest-ready");
    await user.click(within(card("Soup")).getByRole("button", { name: t("suggest.cookTodayFor", { title: "Soup" }) }));
    expect(await within(card("Soup")).findByTestId("suggest-card-error")).toHaveTextContent(t("planner.problem.weekLocked"));

    cookStatus = null;
    addStatus = problem("shopping-list-conflict", 409);
    await user.click(within(card("Tacos")).getByRole("button", { name: t("suggest.cookTodayFor", { title: "Tacos" }) }));
    await user.click(await within(card("Tacos")).findByRole("button", { name: t("suggest.addMissingFor", { title: "Tacos" }) }));
    expect(await within(card("Tacos")).findByTestId("suggest-card-error")).toHaveTextContent(t("suggest.problem.listConflict"));
    // the button stays so the person can try again
    addStatus = problem("recipe-not-planned", 409);
    await user.click(within(card("Tacos")).getByRole("button", { name: t("suggest.addMissingFor", { title: "Tacos" }) }));
    await waitFor(() => expect(within(card("Tacos")).getByTestId("suggest-card-error")).toHaveTextContent(t("suggest.problem.recipeNotPlanned")));
  });

  it("shows a friendly empty state linking to the pantry when the pantry is empty", async () => {
    items = [];
    pantryCount = 0;
    renderPage();
    const empty = await screen.findByTestId("suggest-empty-pantry");
    expect(within(empty).getByRole("link", { name: t("suggest.empty.link") })).toHaveAttribute("href", "/pantry");
    expect(screen.queryByTestId("suggest-none")).toBeNull();
  });

  it("says nothing can be made with at most 2 missing, linking to the recipes, when the pantry has things", async () => {
    items = [];
    renderPage();
    const none = await screen.findByTestId("suggest-none");
    expect(none).toHaveTextContent(t("suggest.none.body"));
    expect(within(none).getByRole("link", { name: t("suggest.none.link") })).toHaveAttribute("href", "/recipes");
  });

  it("shows loading, then a retryable error", async () => {
    const user = userEvent.setup();
    listStatus = 500;
    renderPage();
    expect(await screen.findByText(t("suggest.loading"))).toHaveAttribute("role", "status");
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(t("suggest.loadFailed"));
    listStatus = 200;
    await user.click(screen.getByRole("button", { name: t("suggest.retry") }));
    expect(await screen.findByTestId("suggest-ready")).toBeInTheDocument();
  });

  it("goes to sign-in on a 401", async () => {
    listStatus = 401;
    renderPage();
    expect(await screen.findByText("login page")).toBeInTheDocument();
  });
});

describe("verifier additions (T-100 UI)", () => {
  const cook = (title: string) => within(card(title)).getByRole("button", { name: t("suggest.cookTodayFor", { title }) });

  it("redirects to home in kid mode and never asks for suggestions", async () => {
    kidMode = { householdId: "h1" };
    renderPage();
    expect(await screen.findByText("home page")).toBeInTheDocument();
    expect(calls.some((c) => c.path.endsWith("what-can-i-make"))).toBe(false);
  });

  it("shows the API's scaled amounts for the chosen servings and sends servings=8", async () => {
    const user = userEvent.setup();
    renderPage();
    await screen.findByTestId("suggest-almost");
    items = [
      item({ recipeId: "r-taco", title: "Tacos", servings: 8, missing: [missing({ name: "Cheese", quantities: [{ amount: 400, unitCode: "g", unitName: "Gram" }] })] }),
    ];
    await user.selectOptions(screen.getByLabelText(t("suggest.servingsLabel")), "8");
    await waitFor(() => expect(calls.some((c) => c.query === "?servings=8")).toBe(true));
    await waitFor(() => expect(within(card("Tacos")).getByTestId("suggest-missing")).toHaveTextContent(t("suggest.missingLine", { item: "400 g Cheese" })));
  });

  it("each failure on cook today shows its own message: 400, 403 kid-mode, plain 403, 404 (and refreshes)", async () => {
    const user = userEvent.setup();
    renderPage();
    await screen.findByTestId("suggest-ready");
    const errorText = () => within(card("Soup")).getByTestId("suggest-card-error");

    cookStatus = json({ type: "about:blank", title: "x", status: 400, errors: { servings: ["x"] } }, 400);
    await user.click(cook("Soup"));
    await waitFor(() => expect(errorText()).toHaveTextContent(t("suggest.problem.servings")));

    cookStatus = problem("kid-mode", 403);
    await user.click(cook("Soup"));
    await waitFor(() => expect(errorText()).toHaveTextContent(t("suggest.problem.kidMode")));

    cookStatus = json({ title: "x", status: 403 }, 403);
    await user.click(cook("Soup"));
    await waitFor(() => expect(errorText()).toHaveTextContent(t("problem.forbidden")));

    const gets = () => calls.filter((c) => c.path.endsWith("what-can-i-make") && c.method === "GET").length;
    const before = gets();
    cookStatus = problem("recipe-not-found", 404);
    await user.click(cook("Soup"));
    await waitFor(() => expect(errorText()).toHaveTextContent(t("suggest.problem.recipeGone")));
    await waitFor(() => expect(gets()).toBeGreaterThan(before));
  });

  it("add missing sends no servings (the API uses the planned ones), also after the servings changed", async () => {
    const user = userEvent.setup();
    renderPage();
    await screen.findByTestId("suggest-almost");
    await user.click(cook("Tacos"));
    await within(card("Tacos")).findByTestId("suggest-planned-note");
    items = items.map((i) => (i.recipeId === "r-taco" ? { ...i, plannedToday: true } : i));
    await user.selectOptions(screen.getByLabelText(t("suggest.servingsLabel")), "8");
    await waitFor(() => expect(calls.some((c) => c.query === "?servings=8")).toBe(true));
    await user.click(await within(card("Tacos")).findByRole("button", { name: t("suggest.addMissingFor", { title: "Tacos" }) }));
    await within(card("Tacos")).findByTestId("suggest-added-note");
    expect(calls.find((c) => c.path.endsWith("r-taco/add-missing"))!.body).toBeNull();
  });
});

describe("norwegian strings", () => {
  it("has every suggest key and the entry links in nb", () => {
    registerMessages("nb", "suggest", suggestNb);
    expect(suggestNb["suggest.title"]).toBe("Hva kan jeg lage?");
    expect(suggestNb["suggest.missingLine"]).toBe("mangler: {item}");
    for (const [key, value] of Object.entries(suggestNb)) expect(value, key).not.toBe("");
    expect(nb["pantry.suggestLink"]).toBeTruthy();
    expect(nb["planner.suggestLink"]).toBeTruthy();
  });
});
