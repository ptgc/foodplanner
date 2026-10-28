import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { components } from "../api/schema";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import Household from "./household";
import Ingredients from "./ingredients";

type Role = components["schemas"]["HouseholdRole"];
type Own = components["schemas"]["OwnIngredientResponse"];
type Usage = components["schemas"]["IngredientUsageResponse"];

const me = { userId: "11111111-1111-1111-1111-111111111111", email: "owner@example.test" };
const none: Usage = { recipes: 0, shoppingLists: 0, pantryItems: 0, importDrafts: 0 };
const own = (id: string, name: string, usage: Partial<Usage> = {}): Own => ({
  id,
  name,
  isStaple: false,
  dimension: "Mass",
  usage: { ...none, ...usage },
});

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });

type Call = { method: string; path: string; body: unknown };
let calls: Call[];
let ingredients: Own[];
let failures: Map<string, Response>;
let role: Role;

const callsTo = (method: string, pattern: RegExp) => calls.filter((c) => c.method === method && pattern.test(c.path));

beforeEach(() => {
  role = "Adult";
  calls = [];
  failures = new Map();
  ingredients = [
    own("i1", "Torskefilet eller annen kokt", { recipes: 2, shoppingLists: 1 }),
    own("i2", "Kvernet pepper"),
    own("i3", "Tagliatelle"),
  ];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const url = new URL(request.url);
      const path = url.pathname;
      const method = request.method;
      const body = method === "GET" || method === "DELETE" ? null : await request.json().catch(() => null);
      calls.push({ method, path, body });
      const failure = failures.get(`${method} ${path}`);
      if (failure) {
        failures.delete(`${method} ${path}`);
        return failure;
      }
      if (path === "/api/auth/me") return json(me);
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/households" && method === "GET") return json([{ id: "h1", name: "Home", role }]);
      if (path === "/api/households/h1/members") return json([{ ...me, role, joinedAt: "2031-03-01T10:00:00Z" }]);
      if (path === "/api/households/h1/invites") return json([]);
      if (path === "/api/households/h1/ingredients/own") return json(ingredients);
      if (path === "/api/households/h1/ingredients" && method === "GET") {
        const term = (url.searchParams.get("search") ?? "").toLowerCase();
        return json([
          { id: "g1", name: "Torsk", isStaple: false, dimension: "Mass", isGlobal: true },
          { id: "i1", name: "Torskefilet eller annen kokt", isStaple: false, dimension: "Mass", isGlobal: false },
        ].filter((i) => i.name.toLowerCase().includes(term)));
      }
      const m = path.match(/^\/api\/households\/h1\/ingredients\/([^/]+)(\/merge)?$/);
      if (m && method === "PUT") {
        const item = ingredients.find((i) => i.id === m[1])!;
        item.name = (body as { name: string }).name;
        return json({ id: item.id, name: item.name, isStaple: false, dimension: "Mass", isGlobal: false });
      }
      if (m && method === "DELETE") {
        ingredients = ingredients.filter((i) => i.id !== m[1]);
        return new Response(null, { status: 204 });
      }
      if (m?.[2] && method === "POST") {
        ingredients = ingredients.filter((i) => i.id !== m[1]);
        return json({ target: { id: "g1", name: "Torsk", isStaple: false, dimension: "Mass", isGlobal: true }, moved: none, warnings: ["dimension-differs"] });
      }
      throw new Error(`unexpected ${method} ${path}`);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

function renderPage(element = <Ingredients />, path = "/ingredients") {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <SessionProvider>{element}</SessionProvider>
    </MemoryRouter>,
  );
  return user;
}

const rows = () => screen.getAllByTestId("ingredient-row");

describe("ingredients screen", () => {
  it("lists own ingredients with their usage and filters them", async () => {
    const user = renderPage();
    await screen.findByText("Tagliatelle");

    expect(rows()).toHaveLength(3);
    expect(rows()[0]).toHaveTextContent(t("ingredients.usage", { recipes: 2, lists: 1, pantry: 0, drafts: 0 }));
    expect(rows()[2]).toHaveTextContent(t("ingredients.unused"));
    await user.type(screen.getByLabelText(t("ingredients.search")), "pepp");
    expect(rows()).toHaveLength(1);
    expect(rows()[0]).toHaveTextContent("Kvernet pepper");
  });

  it("renames inline and shows the duplicate-name problem", async () => {
    const user = renderPage();
    await screen.findByText("Tagliatelle");

    await user.click(screen.getByRole("button", { name: t("ingredients.renameFor", { name: "Kvernet pepper" }) }));
    const field = screen.getByLabelText(t("ingredients.renameLabel", { name: "Kvernet pepper" }));
    await user.clear(field);
    await user.type(field, "Pepper");
    failures.set("PUT /api/households/h1/ingredients/i2", json({ status: 409, type: "urn:foodplanner:problem:ingredient-exists" }, 409));
    await user.click(screen.getByRole("button", { name: t("ingredients.save") }));
    expect(await screen.findByRole("alert")).toHaveTextContent(t("recipe.problem.ingredientExists"));

    await user.click(screen.getByRole("button", { name: t("ingredients.save") }));
    expect(await screen.findByTestId("ingredients-notice")).toHaveTextContent(t("ingredients.renamed", { name: "Pepper" }));
    expect(callsTo("PUT", /ingredients\/i2$/).at(-1)?.body).toEqual({ name: "Pepper" });
    expect(await screen.findByText("Pepper")).toBeInTheDocument();
  });

  it("asks before deleting an unused ingredient and removes it", async () => {
    const user = renderPage();
    await screen.findByText("Tagliatelle");

    await user.click(screen.getByRole("button", { name: t("ingredients.deleteFor", { name: "Tagliatelle" }) }));
    const dialog = screen.getByRole("dialog");
    expect(dialog).toHaveTextContent(t("ingredients.deleteTitle", { name: "Tagliatelle" }));
    expect(callsTo("DELETE", /ingredients\/i3$/)).toHaveLength(0);
    await user.click(within(dialog).getByRole("button", { name: t("ingredients.deleteConfirm") }));

    expect(await screen.findByTestId("ingredients-notice")).toHaveTextContent(t("ingredients.deleted", { name: "Tagliatelle" }));
    await waitFor(() => expect(screen.queryByText("Tagliatelle")).toBeNull());
  });

  it("shows the in-use counts from the 409 and suggests merging", async () => {
    const user = renderPage();
    await screen.findByText("Tagliatelle");
    failures.set(
      "DELETE /api/households/h1/ingredients/i1",
      json({ status: 409, type: "urn:foodplanner:problem:ingredient-in-use", usage: { recipes: 2, shoppingLists: 1, pantryItems: 0, importDrafts: 0 } }, 409),
    );

    await user.click(screen.getByRole("button", { name: t("ingredients.deleteFor", { name: "Torskefilet eller annen kokt" }) }));
    await user.click(within(screen.getByRole("dialog")).getByRole("button", { name: t("ingredients.deleteConfirm") }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      t("ingredients.inUse", { name: "Torskefilet eller annen kokt", recipes: 2, lists: 1, pantry: 0, drafts: 0 }),
    );
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("merges into a catalog ingredient picked by search, after a confirmation with the counts", async () => {
    const user = renderPage();
    await screen.findByText("Tagliatelle");

    await user.click(screen.getByRole("button", { name: t("ingredients.mergeFor", { name: "Torskefilet eller annen kokt" }) }));
    await user.type(screen.getByLabelText(t("ingredients.mergeSearch", { name: "Torskefilet eller annen kokt" })), "torsk");
    // The source itself is not offered as a target.
    const pick = await screen.findByRole("button", { name: t("ingredients.mergePick", { name: "Torsk" }) });
    expect(screen.queryByRole("button", { name: t("ingredients.mergePick", { name: "Torskefilet eller annen kokt" }) })).toBeNull();
    await user.click(pick);

    const dialog = screen.getByRole("dialog");
    expect(dialog).toHaveTextContent(
      t("ingredients.mergeBody", { name: "Torskefilet eller annen kokt", target: "Torsk", recipes: 2, lists: 1, pantry: 0, drafts: 0 }),
    );
    expect(callsTo("POST", /merge$/)).toHaveLength(0);
    await user.click(within(dialog).getByRole("button", { name: t("ingredients.mergeConfirm") }));

    await waitFor(() => expect(callsTo("POST", /ingredients\/i1\/merge$/)).toHaveLength(1));
    expect(callsTo("POST", /merge$/)[0]?.body).toEqual({ targetIngredientId: "g1" });
    expect(await screen.findByTestId("ingredients-notice")).toHaveTextContent(
      t("ingredients.merged", { name: "Torskefilet eller annen kokt", target: "Torsk" }),
    );
    expect(screen.getByText(t("ingredients.warning.dimension-differs", { name: "Torskefilet eller annen kokt", target: "Torsk" }))).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText("Torskefilet eller annen kokt", { selector: ".household-row-title" })).toBeNull());
  });

  it("shows a child a note and no ingredient calls; adults and owners get the link in household settings, children do not", async () => {
    role = "Child";
    renderPage();
    expect(await screen.findByText(t("ingredients.adultsOnly"))).toBeInTheDocument();
    expect(calls.filter((c) => c.path.includes("/ingredients"))).toEqual([]);
  });

  it("links to the screen from household settings for adults only", async () => {
    role = "Adult";
    renderPage(<Household />, "/household");
    expect(await screen.findByRole("link", { name: t("ingredients.link") })).toHaveAttribute("href", "/ingredients");
  });

  it("does not show the link to a child", async () => {
    role = "Child";
    renderPage(<Household />, "/household");
    await screen.findByTestId("household-role");
    expect(screen.queryByRole("link", { name: t("ingredients.link") })).toBeNull();
  });
});
