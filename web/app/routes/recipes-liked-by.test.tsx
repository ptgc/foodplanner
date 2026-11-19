import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { json } from "../kid/fake-server";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import Recipes from "./recipes";

const mia = { id: "p-mia", displayName: "Mia", avatar: "fox" };
const ola = { id: "p-ola", displayName: "Ola", avatar: "owl" };

const card = (id: string, title: string, likedBy: (typeof mia)[]) => ({
  id,
  title,
  servings: 4,
  totalMinutes: 20,
  tags: [],
  thumbUrl: null,
  cardUrl: null,
  likedBy,
  liked: false,
  foodType: "Unknown",
});

let queries: string[];

beforeEach(() => {
  queries = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const url = new URL(request.url);
      const path = url.pathname;
      if (path === "/api/auth/me") return json({ userId: "u1", email: "a@example.test", kidMode: null });
      if (path === "/api/households") return json([{ id: "h1", name: "Home", role: "Adult" }]);
      if (path === "/api/households/h1/tags") return json([]);
      if (path === "/api/households/h1/profiles") return json([mia, ola]);
      if (path === "/api/households/h1/recipes") {
        queries.push(url.search);
        const wanted = url.searchParams.getAll("likedByProfileId");
        const all = [card("r1", "Pasta", [mia, ola]), card("r2", "Suppe", [ola]), card("r3", "Fisk", [])];
        const items = all.filter((r) => wanted.every((id) => r.likedBy.some((p) => p.id === id)));
        return json({ items, total: items.length, limit: 12, offset: 0 });
      }
      throw new Error(`unexpected ${request.method} ${path}`);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function Where() {
  const location = useLocation();
  return <output data-testid="url">{location.search}</output>;
}

function renderRecipes() {
  render(
    <MemoryRouter initialEntries={["/recipes"]}>
      <SessionProvider>
        <Where />
        <Routes>
          <Route path="/recipes" element={<Recipes />} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

describe("Recipe list: liked by (T-091)", () => {
  it("shows the kids who like a recipe as small avatars with an accessible list of names", async () => {
    renderRecipes();
    const cards = await screen.findAllByTestId("recipe-card");
    const liked = within(cards[0]!).getByTestId("recipe-card-liked");
    expect(liked).toHaveAccessibleName(t("recipes.likedByNames", { names: "Mia, Ola" }));
    expect(within(cards[1]!).getByTestId("recipe-card-liked")).toHaveAccessibleName(t("recipes.likedByNames", { names: "Ola" }));
    expect(within(cards[2]!).queryByTestId("recipe-card-liked")).not.toBeInTheDocument();
  });

  it("has a filter chip per kid; choosing kids narrows the list to what ALL of them like and is kept in the URL", async () => {
    const u = userEvent.setup();
    renderRecipes();
    const group = await screen.findByRole("group", { name: t("recipes.likedBy") });
    const chips = within(group).getAllByTestId("liked-by-chip");
    expect(chips.map((c) => c.textContent)).toEqual([expect.stringContaining("Mia"), expect.stringContaining("Ola")]);

    await u.click(chips[1]!); // Ola
    await waitFor(() => expect(screen.getAllByTestId("recipe-card")).toHaveLength(2));
    expect(screen.getByTestId("url")).toHaveTextContent("liked=p-ola");
    expect(queries.at(-1)).toContain("likedByProfileId=p-ola");
    expect(chips[1]).toHaveAttribute("aria-pressed", "true");

    await u.click(chips[0]!); // Mia and Ola
    await waitFor(() => expect(screen.getAllByTestId("recipe-card")).toHaveLength(1));
    expect(screen.getByTestId("recipe-card")).toHaveTextContent("Pasta");
  });
});
