import { render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import RecipeDetail from "../routes/recipe-detail";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";

const me = { userId: "11111111-1111-1111-1111-111111111111", email: "adult@example.test" };
const json = (body: unknown) => new Response(JSON.stringify(body), { headers: { "content-type": "application/json" } });

let online: boolean;
let recipeApiUp: boolean;

beforeEach(() => {
  window.localStorage.clear();
  online = false;
  recipeApiUp = false;
  vi.spyOn(navigator, "onLine", "get").mockImplementation(() => online);
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      if (path === "/api/auth/me") return json(me);
      if (path === "/api/households") return json([{ id: "h1", name: "Home", role: "Adult" }]);
      // The service worker answers a recipe that was never opened with a network error while offline.
      if (!recipeApiUp) throw new TypeError("Failed to fetch");
      throw new Error(`unexpected ${path}`);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function renderDetail() {
  return render(
    <MemoryRouter initialEntries={["/recipes/r9"]}>
      <SessionProvider>
        <Routes>
          <Route path="recipes/:id" element={<RecipeDetail />} />
          <Route path="recipes" element={<h1>Recipes page</h1>} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

describe("offline page for a recipe that was never opened", () => {
  it("shows the translated offline notice with a way back, not a raw error", async () => {
    renderDetail();
    const notice = await screen.findByTestId("offline-notice");
    expect(notice).toHaveTextContent(t("pwa.offlineTitle"));
    expect(notice).toHaveTextContent(t("pwa.offlineBody"));
    expect(screen.getByRole("link", { name: t("pwa.offlineRecipes") })).toHaveAttribute("href", "/recipes");
    expect(screen.queryByText(t("problem.unreachable"))).not.toBeInTheDocument();
  });

  it("keeps the plain 'could not reach the API' message when the browser thinks it is online", async () => {
    online = true;
    renderDetail();
    expect(await screen.findByText(t("problem.unreachable"))).toBeInTheDocument();
    expect(screen.queryByTestId("offline-notice")).not.toBeInTheDocument();
  });
});
