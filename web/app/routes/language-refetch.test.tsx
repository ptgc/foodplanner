import { act, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { getLocale, resetLocale, setLocale, type Locale } from "../i18n";
import { SessionProvider } from "../session";
import RecipeDetail from "./recipe-detail";
import Recipes from "./recipes";

// T-078: catalog names (ingredients, tags) come from the API per Accept-Language, so a language switch must read them
// again; a screen that was already loaded may not keep the old language.
const json = (body: unknown) => new Response(JSON.stringify(body), { headers: { "content-type": "application/json" } });
const names: Record<Locale, { rice: string; tag: string }> = { en: { rice: "Rice", tag: "Baking" }, nb: { rice: "Ris", tag: "Baking nb" } };

let asked: { path: string; language: string | null }[];

beforeEach(() => {
  asked = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      const language = request.headers.get("Accept-Language");
      asked.push({ path, language });
      const n = names[(language as Locale) ?? "en"];
      if (path === "/api/auth/me") return json({ userId: "u1", email: "a@example.test" });
      if (path === "/api/auth/antiforgery") return json({ token: "tok" });
      if (path === "/api/households") return json([{ id: "h1", name: "Home", role: "Adult" }]);
      if (path === "/api/households/h1/tags") return json([{ id: "t1", name: n.tag, kind: "Tag", isGlobal: true }]);
      if (path === "/api/households/h1/recipes") return json({ items: [], total: 0 });
      if (path === "/api/households/h1/recipes/r1") {
        return json({
          id: "r1", sourceUrl: null, sourceName: null, title: "Typed title", description: null, servings: 4, prepMinutes: 5, cookMinutes: 5,
          totalMinutes: 10, image: null, tags: [{ id: "t1", name: n.tag, kind: "Tag", isGlobal: true }],
          steps: [{ position: 1, text: "Step" }],
          ingredients: [{ ingredientId: "i1", ingredientName: n.rice, quantity: 200, unitId: "u", unitCode: "g", unitName: "g", note: null, amountKind: "Measured", purpose: "Main", isOptional: false, alternatives: [] }],
        });
      }
      throw new Error(`unexpected ${request.method} ${path}`);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  act(() => resetLocale());
});

function renderAt(url: string) {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <SessionProvider>
        <Routes>
          <Route path="/recipes" element={<Recipes />} />
          <Route path="/recipes/:id" element={<RecipeDetail />} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

const other = (): Locale => (getLocale() === "en" ? "nb" : "en");

describe("a language switch reads catalog names again", () => {
  it("recipe detail: ingredient names change, the typed title does not, and the request carries the new language", async () => {
    const first = getLocale();
    renderAt("/recipes/r1");
    expect(await screen.findByText(new RegExp(names[first].rice))).toBeInTheDocument();

    const second = other();
    act(() => setLocale(second, false));

    await waitFor(() => expect(screen.getByText(new RegExp(names[second].rice))).toBeInTheDocument());
    expect(screen.queryByText(new RegExp(`\\b${names[first].rice}\\b`))).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Typed title");
    const reads = asked.filter((a) => a.path === "/api/households/h1/recipes/r1").map((a) => a.language);
    expect(reads).toEqual([first, second]);
  });

  it("recipe grid: the tag chips change", async () => {
    const first = getLocale();
    renderAt("/recipes");
    const chip = async (locale: Locale) => (await screen.findAllByRole("button", { name: names[locale].tag }))[0]!;
    await chip(first);

    const second = other();
    act(() => setLocale(second, false));

    await waitFor(() => expect(within(document.body).getAllByRole("button", { name: names[second].tag }).length).toBeGreaterThan(0));
    expect(screen.queryByRole("button", { name: names[first].tag })).not.toBeInTheDocument();
  });
});
