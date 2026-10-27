import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { components } from "../api/schema";
import { pageSize } from "../recipes/use-recipes";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import RecipeDetail from "./recipe-detail";
import Recipes, { searchDebounceMs } from "./recipes";

type Summary = components["schemas"]["RecipeSummaryResponse"];
type Tag = components["schemas"]["TagResponse"];
type Recipe = components["schemas"]["RecipeResponse"];

const user = { userId: "11111111-1111-1111-1111-111111111111", email: "admin@example.test" };
const home = { id: "h1", name: "Home", role: "Owner" };

const tag = (id: string, name: string, kind: Tag["kind"] = "Tag"): Tag => ({ id, name, kind, isGlobal: true });
const dinner = tag("dinner", "Middag", "Category");
const dessert = tag("dessert", "Dessert", "Category");
const veg = tag("veg", "Vegetar");
const quick = tag("quick", "Rask");

const recipe = (id: string, title: string, totalMinutes: number | null, tags: Tag[], withImage = false): Summary => ({
  id,
  title,
  servings: 4,
  totalMinutes,
  tags,
  thumbUrl: withImage ? `/img/${id}/thumb` : null,
  cardUrl: withImage ? `/img/${id}/card` : null,
});

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });

type Server = {
  recipes: Summary[];
  tags: Tag[];
  listStatus: number;
  listProblemType: string | undefined;
  down: boolean;
  /** Query strings of every recipe list request, in order. */
  listCalls: string[];
  detail: Recipe | null;
};
let server: Server;

/** Applies the T-023 semantics: AND over tags, contains on the title, max minutes, then offset paging. */
function list(url: URL): Response {
  server.listCalls.push(url.search);
  if (server.listStatus !== 200)
    return json({ title: "x", status: server.listStatus, type: server.listProblemType }, server.listStatus);
  const q = url.searchParams;
  const search = q.get("search")?.toLowerCase();
  const tagIds = q.getAll("tagIds");
  const max = q.get("maxMinutes");
  const matches = server.recipes.filter(
    (r) =>
      (!search || r.title.toLowerCase().includes(search)) &&
      tagIds.every((id) => r.tags.some((x) => x.id === id)) &&
      (max === null || (r.totalMinutes !== null && Number(r.totalMinutes) <= Number(max))),
  );
  const limit = Number(q.get("limit") ?? 20);
  const offset = Number(q.get("offset") ?? 0);
  return json({ items: matches.slice(offset, offset + limit), total: matches.length, limit, offset });
}

beforeEach(() => {
  server = {
    recipes: [
      recipe("r1", "Fiskesuppe", 40, [dinner, quick]),
      recipe("r2", "Grønnsakslasagne", 60, [dinner, veg]),
      recipe("r3", "Pannekaker", 20, [dessert, veg, quick]),
    ],
    tags: [dinner, dessert, veg, quick],
    listStatus: 200,
    listProblemType: undefined,
    down: false,
    listCalls: [],
    detail: null,
  };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      if (server.down) throw new TypeError("Failed to fetch");
      const url = new URL(request.url);
      const path = url.pathname;
      if (path === "/api/auth/me") return json(user);
      if (path === "/api/households") return json([home]);
      if (path === "/api/households/h1/tags") return json(server.tags);
      if (path === "/api/households/h1/recipes") return list(url);
      const detail = path.match(/^\/api\/households\/h1\/recipes\/([^/]+)$/);
      if (detail) return server.detail && server.detail.id === detail[1] ? json(server.detail) : json({ status: 404 }, 404);
      throw new Error(`unexpected ${request.method} ${path}`);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

function Probe() {
  const location = useLocation();
  const navigate = useNavigate();
  return (
    <>
      <output data-testid="url">{location.pathname + location.search}</output>
      <button type="button" onClick={() => void navigate(-1)} data-testid="back">
        back
      </button>
    </>
  );
}

function renderPage(url = "/recipes") {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <SessionProvider>
        <Probe />
        <Routes>
          <Route path="/recipes" element={<Recipes />} />
          <Route path="/recipes/:id" element={<RecipeDetail />} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

const url = () => screen.getByTestId("url").textContent;
const cards = () => screen.getAllByTestId("recipe-card").map((c) => within(c).getByText(/./, { selector: ".recipe-card-title" }).textContent);
const chip = (name: string) => screen.getByRole("button", { name });

async function loaded() {
  await screen.findAllByTestId("recipe-card");
}

describe("Recipes grid", () => {
  it("shows a skeleton first, then a link card per recipe with title, time and tags", async () => {
    renderPage();
    expect(await screen.findByTestId("recipes-skeleton")).toBeInTheDocument();
    await loaded();
    expect(screen.queryByTestId("recipes-skeleton")).toBeNull();
    expect(cards()).toEqual(["Fiskesuppe", "Grønnsakslasagne", "Pannekaker"]);

    const first = screen.getAllByTestId("recipe-card")[0]!;
    expect(first).toHaveAttribute("href", "/recipes/r1");
    expect(within(first).getByText(t("recipes.minutes", { minutes: 40 }))).toBeInTheDocument();
    expect(within(first).getByText("Middag")).toBeInTheDocument();
    expect(within(first).getByText("Rask")).toBeInTheDocument();
    expect(screen.getByText(t("recipes.count", { count: 3 }))).toBeInTheDocument();
  });

  it("shows the real picture when a recipe has one and the placeholder when it has none", async () => {
    server.recipes = [recipe("r1", "Med bilde", 10, [], true), recipe("r2", "Uten bilde", 10, [])];
    renderPage();
    await loaded();
    const [withImage, without] = screen.getAllByTestId("recipe-card");
    const img = withImage!.querySelector("img")!;
    expect(img).toHaveAttribute("src", "/img/r1/card");
    expect(img).toHaveAttribute("loading", "lazy");
    expect(img.getAttribute("srcset")).toContain("/img/r1/thumb 160w");
    expect(without!.querySelector("img")).toBeNull();
    expect(without!.querySelector(".recipe-image-placeholder")).toHaveAttribute("aria-hidden", "true");
  });

  it("offers category, tag and time chips, none pressed at first", async () => {
    renderPage();
    await loaded();
    const categories = screen.getByRole("group", { name: t("recipes.categories") });
    expect(within(categories).getAllByRole("button").map((b) => b.textContent)).toEqual(["Middag", "Dessert"]);
    const tags = screen.getByRole("group", { name: t("recipes.tags") });
    expect(within(tags).getAllByRole("button").map((b) => b.textContent)).toEqual(["Vegetar", "Rask"]);
    for (const minutes of [20, 30, 60]) {
      expect(chip(t("recipes.maxMinutes", { minutes }))).toHaveAttribute("aria-pressed", "false");
    }
    expect(screen.queryByRole("button", { name: t("recipes.clearFilters") })).toBeNull();
  });

  it("combines chips with AND, sends every tag id, and mirrors the filters in the URL", async () => {
    const user = userEvent.setup();
    renderPage();
    await loaded();

    await user.click(chip("Vegetar"));
    await waitFor(() => expect(cards()).toEqual(["Grønnsakslasagne", "Pannekaker"]));
    expect(chip("Vegetar")).toHaveAttribute("aria-pressed", "true");
    expect(url()).toBe("/recipes?tag=veg");

    await user.click(chip("Rask"));
    await waitFor(() => expect(cards()).toEqual(["Pannekaker"]));
    expect(url()).toBe("/recipes?tag=veg&tag=quick");
    const last = new URLSearchParams(server.listCalls.at(-1));
    expect(last.getAll("tagIds")).toEqual(["veg", "quick"]);

    await user.click(chip(t("recipes.maxMinutes", { minutes: 30 })));
    await waitFor(() => expect(url()).toBe("/recipes?tag=veg&tag=quick&max=30"));
    expect(new URLSearchParams(server.listCalls.at(-1)).get("maxMinutes")).toBe("30");

    // Toggling a pressed chip removes it again.
    await user.click(chip("Rask"));
    await waitFor(() => expect(url()).toBe("/recipes?tag=veg&max=30"));
    expect(chip("Rask")).toHaveAttribute("aria-pressed", "false");
  });

  it("starts from the filters in the URL (reload, shared link)", async () => {
    renderPage("/recipes?q=panne&tag=dessert&max=30");
    await loaded();
    expect(cards()).toEqual(["Pannekaker"]);
    expect(chip("Dessert")).toHaveAttribute("aria-pressed", "true");
    expect(chip(t("recipes.maxMinutes", { minutes: 30 }))).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByLabelText(t("recipes.search"))).toHaveValue("panne");
    expect(new URLSearchParams(server.listCalls[0]).get("search")).toBe("panne");
  });

  it("back and forward restore the filters, including the search box", async () => {
    const user = userEvent.setup();
    renderPage("/recipes?q=suppe");
    await loaded();
    expect(cards()).toEqual(["Fiskesuppe"]);

    await user.click(screen.getByRole("button", { name: t("recipes.clearFilters") }));
    await waitFor(() => expect(cards()).toHaveLength(3));
    expect(screen.getByLabelText(t("recipes.search"))).toHaveValue("");

    await user.click(screen.getByTestId("back"));
    await waitFor(() => expect(cards()).toEqual(["Fiskesuppe"]));
    expect(screen.getByLabelText(t("recipes.search"))).toHaveValue("suppe");
    expect(url()).toBe("/recipes?q=suppe");
  });

  it("debounces the search: nothing is sent while typing, one request after the pause", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    renderPage();
    await loaded();
    const calls = server.listCalls.length;

    await user.type(screen.getByLabelText(t("recipes.search")), "pan");
    await act(async () => {
      await vi.advanceTimersByTimeAsync(searchDebounceMs - 100);
    });
    expect(server.listCalls).toHaveLength(calls);
    expect(url()).toBe("/recipes");

    await act(async () => {
      await vi.advanceTimersByTimeAsync(150);
    });
    await waitFor(() => expect(cards()).toEqual(["Pannekaker"]));
    expect(server.listCalls).toHaveLength(calls + 1);
    expect(url()).toBe("/recipes?q=pan");
  });

  it("clear filters resets chips, search and URL", async () => {
    const user = userEvent.setup();
    renderPage("/recipes?q=a&tag=veg&max=60");
    await loaded();
    await user.click(screen.getByRole("button", { name: t("recipes.clearFilters") }));
    await waitFor(() => expect(url()).toBe("/recipes"));
    await waitFor(() => expect(cards()).toHaveLength(3));
    expect(screen.getByLabelText(t("recipes.search"))).toHaveValue("");
    expect(chip("Vegetar")).toHaveAttribute("aria-pressed", "false");
    expect(screen.queryByRole("button", { name: t("recipes.clearFilters") })).toBeNull();
  });

  it("loads more with offset paging until everything is shown", async () => {
    const user = userEvent.setup();
    server.recipes = Array.from({ length: pageSize + 5 }, (_, i) =>
      recipe(`n${String(i).padStart(2, "0")}`, `Rett ${String(i).padStart(2, "0")}`, 15, []),
    );
    renderPage();
    await loaded();
    expect(screen.getAllByTestId("recipe-card")).toHaveLength(pageSize);
    expect(screen.getByText(t("recipes.count", { count: pageSize + 5 }))).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: t("recipes.loadMore") }));
    await waitFor(() => expect(screen.getAllByTestId("recipe-card")).toHaveLength(pageSize + 5));
    const second = new URLSearchParams(server.listCalls.at(-1));
    expect(second.get("offset")).toBe(String(pageSize));
    expect(second.get("limit")).toBe(String(pageSize));
    expect(screen.queryByRole("button", { name: t("recipes.loadMore") })).toBeNull();
  });

  it("keeps the shown recipes and offers a retry when loading more fails", async () => {
    const user = userEvent.setup();
    server.recipes = Array.from({ length: pageSize + 1 }, (_, i) => recipe(`n${i}`, `Rett ${i}`, 15, []));
    renderPage();
    await loaded();
    server.down = true;
    await user.click(screen.getByRole("button", { name: t("recipes.loadMore") }));
    expect(await screen.findByText(new RegExp(t("problem.unreachable")))).toBeInTheDocument();
    expect(screen.getAllByTestId("recipe-card")).toHaveLength(pageSize);

    server.down = false;
    await user.click(screen.getByRole("button", { name: t("recipes.retry") }));
    await waitFor(() => expect(screen.getAllByTestId("recipe-card")).toHaveLength(pageSize + 1));
  });

  it("explains an empty household, and no matches with a way out", async () => {
    server.recipes = [];
    const { unmount } = renderPage();
    expect(await screen.findByText(t("recipes.empty"))).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: t("recipes.clearFilters") })).toBeNull();
    unmount();

    server.recipes = [recipe("r1", "Fiskesuppe", 40, [dinner])];
    renderPage("/recipes?q=zzz");
    expect(await screen.findByText(t("recipes.noMatch"))).toBeInTheDocument();
    expect(screen.getByText(t("recipes.noMatchHint"))).toBeInTheDocument();
    expect(screen.getByRole("button", { name: t("recipes.clearFilters") })).toBeInTheDocument();
  });

  it("shows a translated message for a ProblemDetails type (never the server title) and can retry", async () => {
    const user = userEvent.setup();
    server.listStatus = 403;
    server.listProblemType = "urn:foodplanner:problem:forbidden";
    renderPage();
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(t("recipes.loadFailed"));
    expect(alert).toHaveTextContent(t("problem.forbidden"));
    expect(alert).not.toHaveTextContent("x");

    server.listStatus = 200;
    await user.click(screen.getByRole("button", { name: t("recipes.retry") }));
    await loaded();
    expect(cards()).toHaveLength(3);
  });

  it("says the API is unreachable when the network fails", async () => {
    server.down = true;
    renderPage();
    expect(await screen.findByRole("alert")).toHaveTextContent(t("problem.unreachable"));
  });

  it("still shows the grid when the tag chips cannot be loaded", async () => {
    server.tags = [];
    renderPage();
    await loaded();
    expect(screen.queryByRole("group", { name: t("recipes.categories") })).toBeNull();
    expect(cards()).toHaveLength(3);
  });
});

describe("Recipe detail (minimal, T-025 extends)", () => {
  it("opens from a card and shows title, time, ingredients with quantities and steps", async () => {
    const user = userEvent.setup();
    server.detail = {
      id: "r1",
      sourceUrl: null,
      sourceName: null,
      title: "Fiskesuppe",
      description: null,
      servings: 4,
      prepMinutes: 10,
      cookMinutes: 30,
      totalMinutes: 40,
      image: null,
      steps: [
        { position: 1, text: "Kok opp vannet" },
        { position: 2, text: "Ha i fisken" },
      ],
      ingredients: [
        { ingredientId: "i1", ingredientName: "Torsk", quantity: 400, unitId: "u1", unitCode: "g", unitName: "gram", note: null },
        { ingredientId: "i2", ingredientName: "Salt", quantity: null, unitId: null, unitCode: null, unitName: null, note: "etter smak" },
      ],
      tags: [dinner],
    };
    renderPage();
    await loaded();
    await user.click(screen.getAllByTestId("recipe-card")[0]!);

    expect(await screen.findByRole("heading", { name: "Fiskesuppe", level: 1 })).toBeInTheDocument();
    expect(url()).toBe("/recipes/r1");
    expect(screen.getByText(t("recipes.minutes", { minutes: 40 }))).toBeInTheDocument();
    const items = screen.getAllByRole("listitem");
    expect(items.some((li) => li.textContent?.includes("400 g") && li.textContent.includes("Torsk"))).toBe(true);
    expect(items.some((li) => li.textContent?.includes("Salt (etter smak)"))).toBe(true);
    expect(screen.getByText("Ha i fisken")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: t("recipes.back") })).toHaveAttribute("href", "/recipes");
  });

  it("says so when the recipe does not exist", async () => {
    renderPage("/recipes/nope");
    expect(await screen.findByText(t("recipes.notFound"))).toBeInTheDocument();
  });
});
