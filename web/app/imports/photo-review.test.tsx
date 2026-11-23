import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAntiforgeryToken } from "../api/client";
import type { components } from "../api/schema";
import RecipeImport from "../routes/recipe-import";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";

type Draft = components["schemas"]["RecipeImportResponse"];

const me = { userId: "11111111-1111-1111-1111-111111111111", email: "adult@example.test" };
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" } });

const photoDraft = (patch: Partial<Draft> = {}): Draft => ({
  id: "d1",
  source: "Photo",
  sourceUrl: null,
  status: "Draft",
  title: "Fiskesuppe",
  description: null,
  servings: 4,
  prepMinutes: null,
  cookMinutes: 30,
  imageUrl: null,
  steps: ["Kok opp"],
  lines: [
    {
      position: 0,
      original: "2 dl melk",
      ingredientText: null,
      quantity: 2,
      quantityLow: null,
      unitText: null,
      unitId: "u-dl",
      unitCode: "dl",
      unitName: "desiliter",
      ingredientId: "i-melk",
      ingredientName: "Melk",
      confidence: 1,
      note: null,
      status: "Matched",
      suggestions: [],
      amountKind: "Measured",
      purpose: "Main",
      isOptional: false,
      alternatives: [],
      suggestedProteinClass: null,
      aiSuggested: true,
    },
  ],
  warnings: [],
  canSave: true,
  savedRecipeId: null,
  createdAt: "2026-11-23T10:00:00Z",
  aiAssist: "Applied",
  sourceName: "Kaptein Sabeltann",
  suggestedImage: { url: "/api/households/h1/imports/d1/picture" },
  useSuggestedImage: true,
  ...patch,
});

let draft: Draft;
let puts: Record<string, unknown>[];
let saveBody: Record<string, unknown>;

beforeEach(() => {
  resetAntiforgeryToken();
  draft = photoDraft();
  puts = [];
  saveBody = { recipeId: "r-new", pictureSaved: true };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      if (path === "/api/auth/me") return json(me);
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/households") return json([{ id: "h1", name: "Home", role: "Adult" }]);
      if (path === "/api/units") return json([{ id: "u-dl", code: "dl", name: "desiliter", dimension: "Volume", factorToBase: 100 }]);
      if (path === "/api/households/h1/imports/d1/save") return json(saveBody, 201);
      if (path === "/api/households/h1/imports/d1") {
        if (request.method === "GET") return json(draft);
        const body = (await request.json()) as Record<string, unknown>;
        puts.push(body);
        draft = { ...draft, ...(typeof body.useSuggestedImage === "boolean" ? { useSuggestedImage: body.useSuggestedImage } : {}) };
        return json(draft);
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
  return <output data-testid="url">{location.pathname}</output>;
}

async function openReview() {
  render(
    <MemoryRouter initialEntries={["/recipes/import?draft=d1"]}>
      <SessionProvider>
        <Probe />
        <Routes>
          <Route path="/recipes/import" element={<RecipeImport />} />
          <Route path="/recipes/:id" element={<p>detail</p>} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
  await screen.findByRole("heading", { level: 1, name: t("import.reviewTitle") });
  await screen.findByTestId("photo-source-name");
}

describe("review of a photo import", () => {
  it("shows the AI mark on every line, a photo-specific note and the source book", async () => {
    await openReview();
    expect(screen.getAllByTestId("ai-mark")).toHaveLength(1);
    expect(screen.getByTestId("ai-note")).toHaveTextContent(t("photo.review.note"));
    expect(screen.getByLabelText(t("photo.review.sourceName"))).toHaveValue("Kaptein Sabeltann");
  });

  it("shows the suggested picture with a switch that is on, and saves the choice at once", async () => {
    const user = userEvent.setup();
    await openReview();
    const picture = screen.getByRole("img", { name: t("photo.review.pictureAlt") });
    expect(picture).toHaveAttribute("src", "/api/households/h1/imports/d1/picture");
    const toggle = screen.getByRole("switch", { name: t("photo.review.useSwitch") });
    expect(toggle).toBeChecked();

    await user.click(toggle);
    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]).toMatchObject({ useSuggestedImage: false });
    expect(toggle).not.toBeChecked();
  });

  it("shows no picture block without a suggestion", async () => {
    draft = photoDraft({ suggestedImage: null, useSuggestedImage: false });
    await openReview();
    expect(screen.queryByTestId("photo-suggested")).toBeNull();
    expect(screen.queryByRole("switch")).toBeNull();
  });

  it("saves the edited book name with the draft, then opens the recipe", async () => {
    const user = userEvent.setup();
    await openReview();
    const book = screen.getByLabelText(t("photo.review.sourceName"));
    await user.clear(book);
    await user.type(book, "Fiskeboka");
    await user.click(screen.getByRole("button", { name: t("import.save") }));
    await waitFor(() => expect(screen.getByTestId("url")).toHaveTextContent("/recipes/r-new"));
    expect(puts.at(-1)).toMatchObject({ sourceName: "Fiskeboka", useSuggestedImage: true });
  });

  it("a URL or text import has none of the photo parts", async () => {
    draft = photoDraft({ source: "Text", aiAssist: "NotUsed", suggestedImage: null, useSuggestedImage: false, sourceName: null });
    render(
      <MemoryRouter initialEntries={["/recipes/import?draft=d1"]}>
        <SessionProvider>
          <Routes>
            <Route path="/recipes/import" element={<RecipeImport />} />
          </Routes>
        </SessionProvider>
      </MemoryRouter>,
    );
    await screen.findByRole("heading", { level: 1, name: t("import.reviewTitle") });
    await screen.findByLabelText(t("recipe.fieldTitle"));
    expect(screen.queryByTestId("photo-source-name")).toBeNull();
  });
});
