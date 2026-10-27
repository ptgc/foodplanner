import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { components } from "../api/schema";
import { searchDelayMs } from "../recipes/ingredient-picker";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import RecipeImport from "./recipe-import";
import Recipes from "./recipes";

type Draft = components["schemas"]["RecipeImportResponse"];
type Line = components["schemas"]["ImportLineResponse"];
type Role = components["schemas"]["HouseholdRole"];

const me = { userId: "11111111-1111-1111-1111-111111111111", email: "adult@example.test" };
const urn = (code: string) => `urn:foodplanner:problem:${code}`;
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" } });

const units = [
  { id: "u-g", code: "g", name: "gram", dimension: "Mass", factorToBase: 1 },
  { id: "u-dl", code: "dl", name: "desiliter", dimension: "Volume", factorToBase: 100 },
  { id: "u-stk", code: "stk", name: "stykk", dimension: "Count", factorToBase: 1 },
];
const catalog = [
  { id: "i-melk", name: "Melk", isStaple: false, dimension: "Volume", isGlobal: true },
  { id: "i-salt", name: "Salt", isStaple: true, dimension: "Mass", isGlobal: true },
  { id: "i-tom", name: "Tomat", isStaple: false, dimension: "Count", isGlobal: true },
  { id: "i-snog", name: "Snogras", isStaple: false, dimension: "Count", isGlobal: true },
];

const line = (patch: Partial<Line>): Line => ({
  position: 0,
  original: "",
  ingredientText: null,
  quantity: null,
  quantityLow: null,
  unitText: null,
  unitId: null,
  unitCode: null,
  unitName: null,
  ingredientId: null,
  ingredientName: null,
  confidence: 0,
  note: null,
  status: "UnmatchedIngredient",
  suggestions: [],
  ...patch,
});

const freshDraft = (): Draft => ({
  id: "d1",
  source: "Text",
  sourceUrl: null,
  status: "Draft",
  title: "Tomatsuppe",
  description: null,
  servings: 4,
  prepMinutes: 10,
  cookMinutes: null,
  imageUrl: null,
  steps: ["Kok opp", "Server"],
  lines: [
    line({ original: "2 dl melk", status: "Matched", ingredientId: "i-melk", ingredientName: "Melk", quantity: 2, unitId: "u-dl", unitCode: "dl", confidence: 1 }),
    line({ original: "1 bunt snograss", quantity: 1, unitId: "u-stk", unitCode: "stk", suggestions: [{ ingredientId: "i-snog", name: "Snogras", confidence: 0.6 }] }),
    line({ original: "3 boks tomater", status: "UnmatchedUnit", ingredientId: "i-tom", ingredientName: "Tomat", quantity: 3, unitText: "boks", confidence: 0.9 }),
    line({ original: "salt og vann", quantity: null }),
  ],
  warnings: [],
  canSave: false,
  savedRecipeId: null,
  createdAt: "2026-10-26T10:00:00Z",
});

type Server = {
  role: Role;
  draft: Draft;
  drafts: { id: string; source: "Url" | "Text"; sourceUrl: string | null; title: string; createdAt: string }[];
  createStatus: number;
  createType: string | undefined;
  puts: Record<string, unknown>[];
  /** Answer for the next save POST: a status and body. */
  saveAnswer: { status: number; body: unknown } | null;
  /** Answer for the next PUT that carries line edits. */
  putAnswer: { status: number; body: unknown } | null;
  calls: string[];
  discarded: boolean;
};
let server: Server;

function recompute(d: Draft) {
  d.canSave = d.lines.every((l) => l.status === "Matched");
}

beforeEach(() => {
  server = { role: "Adult", draft: freshDraft(), drafts: [], createStatus: 201, createType: undefined, puts: [], saveAnswer: null, putAnswer: null, calls: [], discarded: false };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const url = new URL(request.url);
      const path = url.pathname;
      server.calls.push(`${request.method} ${path}`);
      if (path === "/api/auth/me") return json(me);
      if (path === "/api/auth/antiforgery") return json({ token: "tok" });
      if (path === "/api/households") return json([{ id: "h1", name: "Home", role: server.role }]);
      if (path === "/api/units") return json(units);
      if (path === "/api/households/h1/tags") return json([]);
      if (path === "/api/households/h1/recipes") return json({ items: [], total: 0, limit: 12, offset: 0 });
      if (path === "/api/households/h1/ingredients") {
        const term = (url.searchParams.get("search") ?? "").toLowerCase();
        return json(catalog.filter((i) => i.name.toLowerCase().includes(term)));
      }
      if (path === "/api/households/h1/imports") {
        if (request.method === "GET") return json(server.drafts);
        if (server.createStatus !== 201) return json({ type: server.createType, status: server.createStatus }, server.createStatus);
        recompute(server.draft);
        return json(server.draft, 201);
      }
      if (path === "/api/households/h1/imports/d1/save") {
        const answer = server.saveAnswer;
        if (answer) return json(answer.body, answer.status);
        return json({ recipeId: "r-new" }, 201);
      }
      if (path === "/api/households/h1/imports/d1") {
        if (request.method === "GET") return json(server.draft);
        if (request.method === "DELETE") {
          server.discarded = true;
          return new Response(null, { status: 204 });
        }
        const body = (await request.json()) as Record<string, unknown> & { lines: { position: number; ingredientId: string | null; quantity: number | null; unitId: string | null; note: string | null; remove: boolean | null }[] | null };
        server.puts.push(body);
        const edits = body.lines ?? [];
        if (edits.length > 0 && server.putAnswer) return json(server.putAnswer.body, server.putAnswer.status);
        for (const edit of edits) {
          if (edit.remove) server.draft.lines.splice(edit.position, 1);
          else {
            const ing = catalog.find((i) => i.id === edit.ingredientId)!;
            const unit = units.find((u) => u.id === edit.unitId);
            server.draft.lines[edit.position] = {
              ...server.draft.lines[edit.position]!,
              status: "Matched",
              ingredientId: ing.id,
              ingredientName: ing.name,
              quantity: edit.quantity,
              unitId: unit?.id ?? null,
              unitCode: unit?.code ?? null,
              unitText: null,
              confidence: 1,
              suggestions: [],
            };
          }
        }
        if (typeof body.title === "string") server.draft.title = body.title;
        recompute(server.draft);
        return json(server.draft);
      }
      throw new Error(`unexpected ${request.method} ${path}`);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function Probe() {
  const location = useLocation();
  return <output data-testid="url">{location.pathname + location.search}</output>;
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <SessionProvider>
        <Probe />
        <Routes>
          <Route path="/recipes" element={<Recipes />} />
          <Route path="/recipes/import" element={<RecipeImport />} />
          <Route path="/recipes/:id" element={<p>detail</p>} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

const url = () => screen.getByTestId("url").textContent;
const cards = () => screen.getAllByTestId("import-line");
const saveButton = () => screen.getByRole("button", { name: t("import.save") });

async function review() {
  renderAt("/recipes/import?draft=d1");
  await screen.findByRole("heading", { level: 1, name: t("import.reviewTitle") });
  await screen.findAllByTestId("import-line");
}

describe("who can reach the import (Child vs Adult)", () => {
  it("a Child gets the translated no-access message and no request", async () => {
    server.role = "Child";
    renderAt("/recipes/import");
    expect(await screen.findByTestId("no-access")).toHaveTextContent(t("import.noAccess"));
    expect(screen.queryByRole("tab")).toBeNull();
    expect(server.calls.some((c) => c.includes("/imports"))).toBe(false);
  });

  it("the recipe grid links to the import for an Adult, not for a Child", async () => {
    renderAt("/recipes");
    const link = await screen.findByTestId("recipe-import-link");
    expect(link).toHaveAttribute("href", "/recipes/import");
    expect(link).toHaveTextContent(t("import.entry"));
  });

  it("the recipe grid has no import link for a Child", async () => {
    server.role = "Child";
    renderAt("/recipes");
    await screen.findByRole("heading", { level: 1 });
    await waitFor(() => expect(server.calls).toContain("GET /api/households"));
    expect(screen.queryByTestId("recipe-import-link")).toBeNull();
  });
});

describe("step 1: paste a web address or text", () => {
  const cases: [number, string | undefined, Parameters<typeof t>[0]][] = [
    [400, urn("import-url-not-allowed"), "import.problem.urlNotAllowed"],
    [422, urn("import-unsupported-content"), "import.problem.unsupportedContent"],
    [422, urn("import-too-large"), "import.problem.tooLarge"],
    [422, urn("import-no-recipe-found"), "import.problem.noRecipeFound"],
    [502, urn("import-fetch-failed"), "import.problem.fetchFailed"],
    [504, urn("import-fetch-timeout"), "import.problem.fetchTimeout"],
    [503, urn("import-busy"), "import.problem.busy"],
    [409, urn("import-limit"), "import.problem.limit"],
    [409, urn("import-not-draft"), "import.problem.notDraft"],
    [404, urn("import-not-found"), "import.problem.notFound"],
    [429, urn("rate-limited"), "import.problem.rateLimited"],
    [429, undefined, "import.problem.rateLimited"],
    [500, undefined, "problem.generic"],
  ];
  it.each(cases)("status %s %s shows the translated message", async (status, type, key) => {
    server.createStatus = status;
    server.createType = type;
    const user = userEvent.setup();
    renderAt("/recipes/import");
    await user.type(await screen.findByLabelText(t("import.urlLabel")), "https://example.com/r");
    await user.click(screen.getByRole("button", { name: t("import.submit") }));
    expect(await screen.findByRole("alert")).toHaveTextContent(t(key));
    expect(url()).toBe("/recipes/import");
  });

  it("no recipe found offers a one-tap switch to the text tab", async () => {
    server.createStatus = 422;
    server.createType = urn("import-no-recipe-found");
    const user = userEvent.setup();
    renderAt("/recipes/import");
    await user.type(await screen.findByLabelText(t("import.urlLabel")), "https://example.com/r");
    await user.click(screen.getByRole("button", { name: t("import.submit") }));
    await user.click(await screen.findByRole("button", { name: t("import.switchToText") }));
    expect(screen.getByRole("tab", { name: t("import.tabText") })).toHaveAttribute("aria-selected", "true");
    await waitFor(() => expect(screen.getByLabelText(t("import.textLabel"))).toHaveFocus());
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("an unreachable API shows the unreachable message", async () => {
    const original = globalThis.fetch;
    const user = userEvent.setup();
    renderAt("/recipes/import");
    await user.type(await screen.findByLabelText(t("import.urlLabel")), "https://example.com/r");
    vi.stubGlobal("fetch", vi.fn(async () => Promise.reject(new TypeError("offline"))));
    await user.click(screen.getByRole("button", { name: t("import.submit") }));
    expect(await screen.findByRole("alert")).toHaveTextContent(t("problem.unreachable"));
    vi.stubGlobal("fetch", original);
  });

  it("empty input is refused before any request", async () => {
    const user = userEvent.setup();
    renderAt("/recipes/import");
    await user.click(await screen.findByRole("button", { name: t("import.submit") }));
    expect(screen.getByText(t("import.error.urlRequired"))).toBeInTheDocument();
    expect(server.calls.some((c) => c.startsWith("POST"))).toBe(false);
  });

  it("pasted text creates a draft and opens the review", async () => {
    const user = userEvent.setup();
    renderAt("/recipes/import");
    await user.click(await screen.findByRole("tab", { name: t("import.tabText") }));
    await user.type(screen.getByLabelText(t("import.textLabel")), "Tomatsuppe");
    await user.click(screen.getByRole("button", { name: t("import.submit") }));
    await screen.findByRole("heading", { level: 1, name: t("import.reviewTitle") });
    expect(url()).toBe("/recipes/import?draft=d1");
    expect(await screen.findByLabelText(t("recipe.fieldTitle"))).toHaveValue("Tomatsuppe");
  });

  it("lists open drafts to resume", async () => {
    server.drafts = [{ id: "d1", source: "Url", sourceUrl: "https://example.com/r", title: "Tomatsuppe", createdAt: "2026-10-26T10:00:00Z" }];
    const user = userEvent.setup();
    renderAt("/recipes/import");
    const item = await screen.findByTestId("import-draft");
    expect(item).toHaveTextContent("Tomatsuppe");
    await user.click(within(item).getByRole("link", { name: t("import.draftContinueLabel", { title: "Tomatsuppe" }) }));
    await screen.findByRole("heading", { level: 1, name: t("import.reviewTitle") });
    expect(url()).toBe("/recipes/import?draft=d1");
  });
});

describe("step 2: review and fix", () => {
  it("shows the original line of every row and marks unmatched ones with an icon and words", async () => {
    await review();
    const rows = cards();
    expect(rows).toHaveLength(4);
    expect(within(rows[0]!).getByText("2 dl melk")).toBeInTheDocument();
    expect(within(rows[0]!).getByTestId("line-status")).toHaveTextContent(t("import.matched"));
    expect(within(rows[0]!).getByText(/Melk/)).toBeInTheDocument();
    expect(within(rows[1]!).getByTestId("line-status")).toHaveTextContent(t("import.status.unmatchedIngredient"));
    expect(within(rows[1]!).getByTestId("line-status")).toHaveTextContent("⚠");
    expect(within(rows[2]!).getByTestId("line-status")).toHaveTextContent(t("import.status.unmatchedUnitNamed", { unit: "boks" }));
    expect(screen.getByTestId("attention-summary")).toHaveTextContent(t("import.needAttention", { count: 3 }));
    expect(saveButton()).toBeDisabled();
  });

  it("the summary links move focus to the line", async () => {
    const user = userEvent.setup();
    await review();
    await user.click(within(screen.getByTestId("attention-summary")).getByRole("button", { name: t("import.goToLine", { n: 3, original: "3 boks tomater" }) }));
    expect(document.getElementById("import-line-2")).toHaveFocus();
  });

  it("fixes a line by picking a suggestion", async () => {
    const user = userEvent.setup();
    await review();
    await user.click(within(cards()[1]!).getByRole("button", { name: t("import.useSuggestion", { name: "Snogras", percent: 60 }) }));
    await waitFor(() => expect(cards()[1]).toHaveAttribute("data-status", "Matched"));
    const edit = (server.puts[0] as { lines: Record<string, unknown>[] }).lines[0]!;
    expect(edit).toMatchObject({ position: 1, ingredientId: "i-snog", quantity: 1, unitId: "u-stk", toTaste: null, note: "" });
    expect(screen.getByTestId("attention-summary")).toHaveTextContent(t("import.needAttention", { count: 2 }));
  });

  it("fixes a line by searching the catalog", async () => {
    const user = userEvent.setup();
    await review();
    const row = cards()[3]!;
    await user.type(within(row).getByLabelText(t("recipe.ingredientSearch")), "sal");
    await user.click(await within(row).findByRole("button", { name: "Salt" }, { timeout: searchDelayMs + 2000 }));
    await user.click(within(row).getByRole("button", { name: t("import.apply") }));
    await waitFor(() => expect(cards()[3]).toHaveAttribute("data-status", "Matched"));
    expect((server.puts[0] as { lines: Record<string, unknown>[] }).lines[0]).toMatchObject({ position: 3, ingredientId: "i-salt", quantity: null, unitId: null, toTaste: true });
  });

  it("fixes an unrecognised unit by choosing one", async () => {
    const user = userEvent.setup();
    await review();
    const row = cards()[2]!;
    await user.click(within(row).getByRole("button", { name: t("import.apply") }));
    // The unit is missing: refused locally, with the message on the field.
    expect(within(row).getByText(t("import.error.lineUnit"))).toBeInTheDocument();
    expect(server.puts).toHaveLength(0);
    await waitFor(() => expect(within(row).getAllByRole("option").length).toBeGreaterThan(1));
    await user.selectOptions(within(row).getByLabelText(t("recipe.unit")), "u-stk");
    await user.click(within(row).getByRole("button", { name: t("import.apply") }));
    await waitFor(() => expect(cards()[2]).toHaveAttribute("data-status", "Matched"));
    expect((server.puts[0] as { lines: Record<string, unknown>[] }).lines[0]).toMatchObject({ position: 2, ingredientId: "i-tom", quantity: 3, unitId: "u-stk", toTaste: null });
  });

  it("makes a line to taste", async () => {
    const user = userEvent.setup();
    await review();
    const row = cards()[2]!;
    await user.click(within(row).getByLabelText(t("recipe.toTaste")));
    await user.click(within(row).getByRole("button", { name: t("import.apply") }));
    await waitFor(() => expect(cards()[2]).toHaveAttribute("data-status", "Matched"));
    expect((server.puts[0] as { lines: Record<string, unknown>[] }).lines[0]).toMatchObject({ position: 2, ingredientId: "i-tom", quantity: null, unitId: null, toTaste: true });
  });

  it("removes a line", async () => {
    const user = userEvent.setup();
    await review();
    await user.click(within(cards()[3]!).getByRole("button", { name: t("import.removeLineLabel", { n: 4 }) }));
    await waitFor(() => expect(cards()).toHaveLength(3));
    expect((server.puts[0] as { lines: Record<string, unknown>[] }).lines[0]).toMatchObject({ position: 3, remove: true });
    expect(screen.getByTestId("attention-summary")).toHaveTextContent(t("import.needAttention", { count: 2 }));
  });

  it("save stays disabled until every line is matched, then saves and opens the recipe", async () => {
    const user = userEvent.setup();
    await review();
    expect(saveButton()).toBeDisabled();
    await user.click(within(cards()[3]!).getByRole("button", { name: t("import.removeLineLabel", { n: 4 }) }));
    await waitFor(() => expect(cards()).toHaveLength(3));
    await user.click(within(cards()[1]!).getByRole("button", { name: t("import.useSuggestion", { name: "Snogras", percent: 60 }) }));
    await waitFor(() => expect(cards()[1]).toHaveAttribute("data-status", "Matched"));
    expect(saveButton()).toBeDisabled();
    await user.click(within(cards()[2]!).getByLabelText(t("recipe.toTaste")));
    await user.click(within(cards()[2]!).getByRole("button", { name: t("import.apply") }));
    await waitFor(() => expect(saveButton()).toBeEnabled());
    expect(screen.getByTestId("attention-summary")).toHaveTextContent(t("import.allMatched"));
    await user.clear(screen.getByLabelText(t("recipe.fieldTitle")));
    await user.type(screen.getByLabelText(t("recipe.fieldTitle")), "Ny suppe");
    await user.click(saveButton());
    await waitFor(() => expect(url()).toBe("/recipes/r-new"));
    const header = server.puts.at(-1)!;
    expect(header).toMatchObject({ title: "Ny suppe", servings: 4, prepMinutes: 10, steps: ["Kok opp", "Server"] });
    expect(server.calls.at(-1)).toBe("POST /api/households/h1/imports/d1/save");
  });

  it("maps a server error on lines[i] to that row", async () => {
    server.draft.lines = server.draft.lines.slice(0, 1).concat(server.draft.lines[0]!);
    server.draft.lines[1] = { ...server.draft.lines[1]!, original: "2 dl melk (igjen)" };
    server.draft.canSave = true;
    server.saveAnswer = {
      status: 400,
      body: { type: "urn:foodplanner:problem:validation", status: 400, errors: { "lines[1].unitId": ["Choose a unit for '2 dl melk'."] } },
    };
    const user = userEvent.setup();
    await review();
    await user.click(saveButton());
    const summary = await screen.findByTestId("error-summary");
    expect(summary).toHaveTextContent(t("import.error.lineUnit"));
    expect(within(cards()[1]!).getByText(t("import.error.lineUnit"))).toBeInTheDocument();
    expect(within(cards()[0]!).queryByText(t("import.error.lineUnit"))).toBeNull();
    expect(url()).toBe("/recipes/import?draft=d1");
  });

  it("maps a server error on an edit to the edited row", async () => {
    server.putAnswer = {
      status: 400,
      body: { type: "urn:foodplanner:problem:validation", status: 400, errors: { "lines[0].ingredientId": ["Unknown."] } },
    };
    const user = userEvent.setup();
    await review();
    await user.click(within(cards()[1]!).getByRole("button", { name: t("import.useSuggestion", { name: "Snogras", percent: 60 }) }));
    expect(await within(cards()[1]!).findByText(t("import.error.lineIngredient"))).toBeInTheDocument();
    expect(cards()[1]).toHaveAttribute("data-status", "UnmatchedIngredient");
  });

  it("a header error from the server (title) lands on the title field", async () => {
    server.draft.lines = [server.draft.lines[0]!];
    server.draft.canSave = true;
    server.putAnswer = null;
    const user = userEvent.setup();
    await review();
    await user.clear(screen.getByLabelText(t("recipe.fieldTitle")));
    await user.click(saveButton());
    expect(await screen.findByTestId("error-summary")).toHaveTextContent(t("recipe.error.title", { max: 200 }));
    expect(screen.getByLabelText(t("recipe.fieldTitle"))).toHaveAttribute("aria-invalid", "true");
    expect(server.puts).toHaveLength(0);
  });

  it("discard asks first, then deletes the draft", async () => {
    const user = userEvent.setup();
    await review();
    await user.click(screen.getByRole("button", { name: t("import.discard") }));
    await user.click(screen.getByRole("button", { name: t("import.discardKeep") }));
    expect(server.discarded).toBe(false);
    await user.click(screen.getByRole("button", { name: t("import.discard") }));
    await user.click(screen.getByRole("button", { name: t("import.discardYes") }));
    await waitFor(() => expect(server.discarded).toBe(true));
    await waitFor(() => expect(url()).toBe("/recipes/import"));
  });

  it("a draft that is gone shows a message and a way back", async () => {
    server.draft.status = "Saved";
    renderAt("/recipes/import?draft=d1");
    expect(await screen.findByText(t("import.reviewGone"))).toBeInTheDocument();
    expect(screen.getByRole("link", { name: t("import.startOver") })).toHaveAttribute("href", "/recipes/import");
  });
});
