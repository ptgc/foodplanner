import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { components } from "../api/schema";
import { initialsOf } from "../planner/avatar";
import { SessionProvider } from "../session";
import { addWeeks, mondayOf } from "../shopping/week";
import { n, t } from "../test-i18n";
import Plan from "./plan";

type Entry = components["schemas"]["MealPlanEntryResponse"];
type Summary = components["schemas"]["RecipeSummaryResponse"];
type Tag = components["schemas"]["TagResponse"];

const me = { userId: "11111111-1111-1111-1111-111111111111", email: "anne.berit@example.test" };
const other = { userId: "22222222-2222-2222-2222-222222222222", email: "per@example.test" };
const week = "2031-03-03"; // a Monday

const dinner: Tag = { id: "dinner", name: "Middag", kind: "Category", isGlobal: true };
const dessert: Tag = { id: "dessert", name: "Dessert", kind: "Category", isGlobal: true };

const recipe = (id: string, title: string, tags: Tag[] = [], servings = 4): Summary => ({
  id,
  title,
  servings,
  totalMinutes: 30,
  tags,
  thumbUrl: null,
  cardUrl: null,
});

const entry = (id: string, date: string, r: Summary, servings: number, pickedBy = me): Entry => ({
  id,
  date,
  recipeId: r.id,
  recipeTitle: r.title,
  servings,
  pickedBy,
});

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });
const problem = (status: number, code?: string) =>
  json({ status, title: "x", ...(code ? { type: `urn:foodplanner:problem:${code}` } : {}) }, status);

type Server = {
  role: "Owner" | "Adult" | "Child";
  recipes: Summary[];
  entries: Entry[];
  /** Forces the next write (post, put, delete, generate) to answer with this. */
  writeError: Response | null;
  planStatus: number;
  imageFor: Record<string, { thumbUrl: string; cardUrl: string }>;
  planCalls: string[];
  writes: { method: string; path: string; body: unknown }[];
  recipeQueries: string[];
};
let server: Server;
let nextId = 1;

beforeEach(() => {
  nextId = 1;
  const fishSoup = recipe("r1", "Fiskesuppe", [dinner]);
  const pancakes = recipe("r2", "Pannekaker", [dessert], 6);
  server = {
    role: "Adult",
    recipes: [fishSoup, pancakes],
    entries: [],
    writeError: null,
    planStatus: 200,
    imageFor: {},
    planCalls: [],
    writes: [],
    recipeQueries: [],
  };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const url = new URL(request.url);
      const path = url.pathname;
      const method = request.method;
      if (path === "/api/auth/me") return json(me);
      if (path === "/api/households") return json([{ id: "h1", name: "Home", role: server.role }]);
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/households/h1/tags") return json([dinner, dessert]);
      if (path === "/api/households/h1/recipes") {
        server.recipeQueries.push(url.search);
        const search = url.searchParams.get("search")?.toLowerCase();
        const tagIds = url.searchParams.getAll("tagIds");
        const items = server.recipes.filter(
          (r) => (!search || r.title.toLowerCase().includes(search)) && tagIds.every((id) => r.tags.some((x) => x.id === id)),
        );
        return json({ items, total: items.length, limit: 12, offset: 0 });
      }
      const detail = path.match(/^\/api\/households\/h1\/recipes\/([^/]+)$/);
      if (detail) {
        const img = server.imageFor[detail[1]!];
        return json({ id: detail[1], title: "x", image: img ? { ...img, fullUrl: img.cardUrl } : null });
      }
      const plan = path.match(/^\/api\/households\/h1\/meal-plans\/([^/]+)(?:\/entries(?:\/([^/]+))?)?$/);
      if (plan) {
        const [, weekStart, entryId] = plan;
        const isEntries = path.includes("/entries");
        if (method === "GET") {
          server.planCalls.push(weekStart!);
          if (server.planStatus !== 200) return problem(server.planStatus);
          return json({ id: null, weekStart, entries: weekStart === week ? server.entries : [] });
        }
        const body = method === "DELETE" ? null : await request.json();
        server.writes.push({ method, path, body });
        if (server.writeError) {
          const failure = server.writeError;
          server.writeError = null;
          return failure;
        }
        if (isEntries && method === "POST") {
          const b = body as { date: string; recipeId: string };
          const r = server.recipes.find((x) => x.id === b.recipeId)!;
          const created = entry(`e${nextId++}`, b.date, r, Number(r.servings));
          server.entries.push(created);
          return json(created, 201);
        }
        if (isEntries && method === "PUT") {
          const e = server.entries.find((x) => x.id === entryId)!;
          e.servings = (body as { servings: number }).servings;
          return json(e);
        }
        if (isEntries && method === "DELETE") {
          server.entries = server.entries.filter((x) => x.id !== entryId);
          return new Response(null, { status: 204 });
        }
      }
      if (path === `/api/households/h1/shopping-lists/${week}/generate` && method === "POST") {
        server.writes.push({ method, path, body: null });
        if (server.writeError) {
          const failure = server.writeError;
          server.writeError = null;
          return failure;
        }
        return json({});
      }
      throw new Error(`unexpected ${method} ${path}`);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function Where() {
  const location = useLocation();
  return <output data-testid="url">{location.pathname + location.search}</output>;
}

function renderPlan(url = `/plan?week=${week}`) {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <SessionProvider>
        <Where />
        <Routes>
          <Route path="/plan" element={<Plan />} />
          <Route path="/shopping" element={<p>shopping page</p>} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

const day = (date: string) => document.querySelector<HTMLElement>(`[data-testid="planner-day"][data-date="${date}"]`)!;
const loaded = () => screen.findAllByTestId("planner-day");
const plus = (date: string) => day(date).querySelector<HTMLButtonElement>(".planner-add")!;
const picker = () => screen.getByRole("dialog");

describe("Week planner: week navigation", () => {
  it("shows Monday to Sunday of the requested week with a localised label", async () => {
    renderPlan();
    const days = await loaded();
    expect(days).toHaveLength(7);
    expect(days.map((d) => d.getAttribute("data-date"))).toEqual([
      "2031-03-03",
      "2031-03-04",
      "2031-03-05",
      "2031-03-06",
      "2031-03-07",
      "2031-03-08",
      "2031-03-09",
    ]);
    const label = screen.getByTestId("planner-week");
    expect(label).toHaveTextContent(t("planner.week", { week: "10", year: "2031" }));
    // The day names come from Intl in the active locale, not from our message files.
    const mondayName = new Intl.DateTimeFormat(document.documentElement.lang, { weekday: "long" }).format(new Date(2031, 2, 3));
    expect(within(days[0]!).getByRole("heading")).toHaveTextContent(new RegExp(mondayName, "i"));
  });

  it("defaults to the current ISO week", async () => {
    renderPlan("/plan");
    await loaded();
    expect(server.planCalls).toEqual([mondayOf(new Date())]);
  });

  it("moves to the previous and next week and keeps the week in the URL", async () => {
    const user = userEvent.setup();
    renderPlan();
    await loaded();
    await user.click(screen.getByRole("button", { name: t("planner.nextWeek") }));
    await waitFor(() => expect(screen.getByTestId("url")).toHaveTextContent(`/plan?week=${addWeeks(week, 1)}`));
    await waitFor(() => expect(server.planCalls).toContain(addWeeks(week, 1)));
    expect(screen.getByTestId("planner-week")).toHaveTextContent(t("planner.week", { week: "11", year: "2031" }));
    await user.click(screen.getByRole("button", { name: t("planner.previousWeek") }));
    await user.click(screen.getByRole("button", { name: t("planner.previousWeek") }));
    await waitFor(() => expect(screen.getByTestId("url")).toHaveTextContent(`/plan?week=${addWeeks(week, -1)}`));
    expect(screen.getByTestId("planner-week")).toHaveTextContent(t("planner.week", { week: "9", year: "2031" }));
    // Away from this week a shortcut back appears.
    await user.click(screen.getByRole("button", { name: t("planner.thisWeek") }));
    await waitFor(() => expect(screen.getByTestId("url")).toHaveTextContent(`/plan?week=${mondayOf(new Date())}`));
  });

  it("falls back to the current week for a date that is not a Monday", async () => {
    renderPlan("/plan?week=2031-03-04");
    await loaded();
    expect(server.planCalls).toEqual([mondayOf(new Date())]);
  });
});

describe("Week planner: planned meals", () => {
  it("shows each meal on its day with image or placeholder, servings and who picked it", async () => {
    server.entries = [
      entry("e1", "2031-03-05", server.recipes[0]!, 4),
      entry("e2", "2031-03-05", server.recipes[1]!, 6, other),
    ];
    server.imageFor = { r1: { thumbUrl: "/img/r1/thumb", cardUrl: "/img/r1/card" } };
    renderPlan();
    await loaded();
    const wednesday = await waitFor(() => {
      const d = day("2031-03-05");
      expect(within(d).getAllByTestId("planner-entry")).toHaveLength(2);
      return d;
    });
    expect(within(day("2031-03-03")).getByText(t("planner.nothingPlanned"))).toBeInTheDocument();

    const [soup, pancakes] = within(wednesday).getAllByTestId("planner-entry");
    expect(within(soup!).getByText("Fiskesuppe")).toBeInTheDocument();
    expect(within(soup!).getByTestId("servings")).toHaveTextContent(t("planner.servings", { count: n(4) }));
    await waitFor(() => expect(soup!.querySelector("img")).toHaveAttribute("src", "/img/r1/card"));
    expect(pancakes!.querySelector("img")).toBeNull();
    expect(pancakes!.querySelector(".recipe-image-placeholder")).not.toBeNull();

    const mine = within(soup!).getByRole("img", { name: t("planner.pickedBy", { who: me.email }) });
    expect(mine).toHaveTextContent("AB");
    expect(within(pancakes!).getByRole("img", { name: t("planner.pickedBy", { who: other.email }) })).toHaveTextContent("P");
    expect(mine.style.backgroundColor).not.toBe("");
  });

  it("derives initials from the email name part", () => {
    expect(initialsOf("anne.berit@x.no", "u")).toBe("AB");
    expect(initialsOf("per@x.no", "u")).toBe("PE");
    expect(initialsOf(null, "9abc")).toBe("9");
    expect(initialsOf("", "")).toBe("?");
  });
});

describe("Week planner: picking a meal", () => {
  it("adds a meal in two taps: the day's plus, then the recipe", async () => {
    const user = userEvent.setup();
    renderPlan();
    await loaded();

    await user.click(plus("2031-03-05"));
    const wednesdayName = new Intl.DateTimeFormat(document.documentElement.lang, { weekday: "long" }).format(new Date(2031, 2, 5));
    expect(picker().getAttribute("aria-labelledby")).not.toBeNull();
    expect(within(picker()).getByRole("heading")).toHaveTextContent(new RegExp(wednesdayName, "i"));
    const card = await screen.findByRole("button", { name: t("planner.pickRecipe", { title: "Pannekaker" }) });
    await user.click(card);

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(server.writes).toEqual([
      { method: "POST", path: `/api/households/h1/meal-plans/${week}/entries`, body: { date: "2031-03-05", recipeId: "r2", servings: null } },
    ]);
    const added = within(day("2031-03-05")).getByTestId("planner-entry");
    expect(within(added).getByText("Pannekaker")).toBeInTheDocument();
    expect(within(added).getByTestId("servings")).toHaveTextContent(t("planner.servings", { count: n(6) }));
    expect(screen.getByTestId("planner-notice")).toHaveTextContent("Pannekaker");
    // Focus goes back to the plus that opened the sheet.
    await waitFor(() => expect(plus("2031-03-05")).toHaveFocus());
  });

  it("searches and filters by category in the picker", async () => {
    const user = userEvent.setup();
    renderPlan();
    await loaded();
    await user.click(plus("2031-03-04"));
    await screen.findAllByTestId("picker-card");
    expect(screen.getAllByTestId("picker-card")).toHaveLength(2);

    await user.click(within(picker()).getByRole("button", { name: "Dessert" }));
    await waitFor(() => expect(screen.getAllByTestId("picker-card")).toHaveLength(1));
    expect(new URLSearchParams(server.recipeQueries.at(-1)).getAll("tagIds")).toEqual(["dessert"]);

    await user.click(within(picker()).getByRole("button", { name: "Dessert" }));
    await user.type(within(picker()).getByRole("searchbox"), "fisk");
    await waitFor(() => expect(new URLSearchParams(server.recipeQueries.at(-1)).get("search")).toBe("fisk"));
    await waitFor(() => expect(screen.getAllByTestId("picker-card")).toHaveLength(1));
    expect(screen.getByRole("button", { name: t("planner.pickRecipe", { title: "Fiskesuppe" }) })).toBeInTheDocument();

    await user.clear(within(picker()).getByRole("searchbox"));
    await user.type(within(picker()).getByRole("searchbox"), "zzz");
    expect(await screen.findByText(t("planner.pickerNoMatch"))).toBeInTheDocument();
  });

  it("closes with the close button and with Escape without adding anything", async () => {
    const user = userEvent.setup();
    renderPlan();
    await loaded();
    await user.click(plus("2031-03-04"));
    await user.click(within(picker()).getByRole("button", { name: t("planner.pickerClose") }));
    expect(screen.queryByRole("dialog")).toBeNull();
    await user.click(plus("2031-03-04"));
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(server.writes).toEqual([]);
  });

  it("says so when there are no recipes", async () => {
    server.recipes = [];
    const user = userEvent.setup();
    renderPlan();
    await loaded();
    await user.click(plus("2031-03-04"));
    expect(await screen.findByText(t("planner.pickerEmpty"))).toBeInTheDocument();
  });
});

describe("Week planner: servings stepper", () => {
  const stepper = (title: string) => ({
    fewer: screen.getByRole("button", { name: t("planner.fewer", { title }) }),
    more: screen.getByRole("button", { name: t("planner.more", { title }) }),
  });

  it("changes servings by one with PUT and shows the saved value", async () => {
    server.entries = [entry("e1", "2031-03-04", server.recipes[0]!, 4)];
    const user = userEvent.setup();
    renderPlan();
    await screen.findByText("Fiskesuppe");
    await user.click(stepper("Fiskesuppe").more);
    await waitFor(() => expect(screen.getByTestId("servings")).toHaveTextContent(t("planner.servings", { count: n(5) })));
    await user.click(stepper("Fiskesuppe").fewer);
    await user.click(stepper("Fiskesuppe").fewer);
    await waitFor(() => expect(screen.getByTestId("servings")).toHaveTextContent(t("planner.servings", { count: n(3) })));
    expect(server.writes.map((w) => [w.method, (w.body as { servings: number }).servings])).toEqual([
      ["PUT", 5],
      ["PUT", 4],
      ["PUT", 3],
    ]);
    expect(server.writes[0]!.body).toEqual({ date: "2031-03-04", servings: 5 });
  });

  it("stops at 1 and at 50", async () => {
    server.entries = [
      entry("e1", "2031-03-04", server.recipes[0]!, 1),
      entry("e2", "2031-03-05", server.recipes[1]!, 50),
    ];
    renderPlan();
    await screen.findByText("Fiskesuppe");
    expect(stepper("Fiskesuppe").fewer).toBeDisabled();
    expect(stepper("Fiskesuppe").more).toBeEnabled();
    expect(stepper("Pannekaker").more).toBeDisabled();
    expect(stepper("Pannekaker").fewer).toBeEnabled();
  });
});

describe("Week planner: removing and roles", () => {
  const removeButton = (title: string, date: string) =>
    within(day(date)).getByRole("article", { name: title }).querySelector<HTMLButtonElement>(".planner-remove");

  beforeEach(() => {
    server.entries = [
      entry("e1", "2031-03-04", server.recipes[0]!, 4, me),
      entry("e2", "2031-03-05", server.recipes[1]!, 4, other),
    ];
  });

  it("lets an Adult remove any entry", async () => {
    const user = userEvent.setup();
    renderPlan();
    await screen.findByText("Pannekaker");
    expect(removeButton("Fiskesuppe", "2031-03-04")).not.toBeNull();
    await user.click(removeButton("Pannekaker", "2031-03-05")!);
    await waitFor(() => expect(screen.queryByText("Pannekaker")).toBeNull());
    expect(server.writes).toEqual([
      { method: "DELETE", path: `/api/households/h1/meal-plans/${week}/entries/e2`, body: null },
    ]);
    expect(screen.getByTestId("planner-notice")).toHaveTextContent(t("planner.removed", { title: "Pannekaker" }));
  });

  it("lets a Child remove and change only their own entries", async () => {
    server.role = "Child";
    renderPlan();
    await screen.findByText("Pannekaker");
    expect(removeButton("Fiskesuppe", "2031-03-04")).not.toBeNull();
    expect(screen.getByRole("button", { name: t("planner.more", { title: "Fiskesuppe" }) })).toBeInTheDocument();
    // Somebody else's pick: no remove button and no stepper, just the number of servings.
    expect(removeButton("Pannekaker", "2031-03-05")).toBeNull();
    expect(screen.queryByRole("button", { name: t("planner.more", { title: "Pannekaker" }) })).toBeNull();
    expect(within(day("2031-03-05")).getByTestId("servings")).toHaveTextContent(t("planner.servings", { count: n(4) }));
    // A Child can still pick meals.
    expect(plus("2031-03-05")).toBeEnabled();
  });
});

describe("Week planner: errors", () => {
  it("shows a translated message when the plan cannot be loaded and retries", async () => {
    server.planStatus = 403;
    const user = userEvent.setup();
    renderPlan();
    expect(await screen.findByText(t("planner.loadFailed"))).toBeInTheDocument();
    server.planStatus = 200;
    await user.click(screen.getByRole("button", { name: t("planner.retry") }));
    expect(await screen.findAllByTestId("planner-day")).toHaveLength(7);
  });

  it("keeps the picker open and explains a rejected add (400)", async () => {
    const user = userEvent.setup();
    renderPlan();
    await loaded();
    await user.click(plus("2031-03-05"));
    server.writeError = json({ status: 400, title: "Bad", errors: { recipeId: ["x"] } }, 400);
    await user.click(await screen.findByRole("button", { name: t("planner.pickRecipe", { title: "Pannekaker" }) }));
    expect(await screen.findByTestId("picker-problem")).toHaveTextContent(t("planner.badRequest"));
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    // The sheet is usable again.
    expect(screen.getByRole("button", { name: t("planner.pickRecipe", { title: "Fiskesuppe" }) })).toBeEnabled();
  });

  it("explains a refused change (403) and keeps the entry as it was", async () => {
    server.entries = [entry("e1", "2031-03-04", server.recipes[0]!, 4, other)];
    const user = userEvent.setup();
    renderPlan();
    await screen.findByText("Fiskesuppe");
    server.writeError = problem(403, "forbidden");
    await user.click(screen.getByRole("button", { name: t("planner.more", { title: "Fiskesuppe" }) }));
    expect(await screen.findByTestId("planner-problem")).toHaveTextContent(t("problem.forbidden"));
    expect(screen.getByTestId("servings")).toHaveTextContent(t("planner.servings", { count: n(4) }));
  });

  it("explains a vanished entry (404) and reads the week again", async () => {
    server.entries = [entry("e1", "2031-03-04", server.recipes[0]!, 4)];
    const user = userEvent.setup();
    renderPlan();
    await screen.findByText("Fiskesuppe");
    server.entries = [];
    server.writeError = problem(404, "meal-plan-entry-not-found");
    await user.click(screen.getByRole("button", { name: t("planner.more", { title: "Fiskesuppe" }) }));
    expect(await screen.findByTestId("planner-problem")).toHaveTextContent(t("problem.mealPlanEntryNotFound"));
    await waitFor(() => expect(screen.queryByText("Fiskesuppe")).toBeNull());
    expect(server.planCalls.length).toBe(2);
  });

  it("explains a conflict (409)", async () => {
    server.entries = [entry("e1", "2031-03-04", server.recipes[0]!, 4)];
    const user = userEvent.setup();
    renderPlan();
    await screen.findByText("Fiskesuppe");
    server.writeError = problem(409, "meal-plan-entry-conflict");
    await user.click(screen.getByRole("button", { name: t("planner.more", { title: "Fiskesuppe" }) }));
    expect(await screen.findByTestId("planner-problem")).toHaveTextContent(t("problem.mealPlanEntryConflict"));
  });

  it("says the API is unreachable when a write cannot be sent", async () => {
    server.entries = [entry("e1", "2031-03-04", server.recipes[0]!, 4)];
    const user = userEvent.setup();
    renderPlan();
    await screen.findByText("Fiskesuppe");
    const realFetch = globalThis.fetch;
    vi.stubGlobal("fetch", async (request: Request) => {
      if (request.method === "PUT") throw new TypeError("Failed to fetch");
      return realFetch(request);
    });
    await user.click(screen.getByRole("button", { name: t("planner.more", { title: "Fiskesuppe" }) }));
    expect(await screen.findByTestId("planner-problem")).toHaveTextContent(t("problem.unreachable"));
  });
});

describe("Week planner: shopping list", () => {
  it("links to the shopping list of the shown week", async () => {
    renderPlan();
    await loaded();
    expect(screen.getByRole("link", { name: t("planner.shoppingList") })).toHaveAttribute("href", `/shopping?week=${week}`);
  });

  it("generates the list and then opens it", async () => {
    server.entries = [entry("e1", "2031-03-04", server.recipes[0]!, 4)];
    const user = userEvent.setup();
    renderPlan();
    await screen.findByText("Fiskesuppe");
    await user.click(screen.getByRole("button", { name: t("planner.generate") }));
    await waitFor(() => expect(screen.getByTestId("url")).toHaveTextContent(`/shopping?week=${week}`));
    expect(server.writes.at(-1)).toMatchObject({ method: "POST", path: `/api/households/h1/shopping-lists/${week}/generate` });
  });

  it("stays on the plan and says so when generating fails", async () => {
    server.entries = [entry("e1", "2031-03-04", server.recipes[0]!, 4)];
    const user = userEvent.setup();
    renderPlan();
    await screen.findByText("Fiskesuppe");
    server.writeError = problem(500);
    await user.click(screen.getByRole("button", { name: t("planner.generate") }));
    expect(await screen.findByTestId("planner-problem")).toHaveTextContent(t("planner.generateFailed"));
    expect(screen.getByTestId("url")).toHaveTextContent("/plan");
  });

  it("does not offer to generate an empty week", async () => {
    renderPlan();
    await loaded();
    expect(screen.getByRole("button", { name: t("planner.generate") })).toBeDisabled();
  });
});
