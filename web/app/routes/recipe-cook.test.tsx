import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { components } from "../api/schema";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import RecipeCook from "./recipe-cook";
import RecipeDetail from "./recipe-detail";

type Recipe = components["schemas"]["RecipeResponse"];

const me = { userId: "11111111-1111-1111-1111-111111111111", email: "adult@example.test" };

const recipe: Recipe = {
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
  tags: [],
  steps: [
    { position: 1, text: "Kok opp vannet" },
    { position: 2, text: "Ha i fisken" },
    { position: 3, text: "Server" },
  ],
  ingredients: [
    { ingredientId: "i-torsk", ingredientName: "Torsk", quantity: 400, unitId: "u-g", unitCode: "g", unitName: "gram", note: null },
    { ingredientId: "i-salt", ingredientName: "Salt", quantity: null, unitId: null, unitCode: null, unitName: null, note: "etter smak" },
  ],
};

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });

let scaledCalls: string[];
let scaleStatus: number;

beforeEach(() => {
  scaledCalls = [];
  scaleStatus = 200;
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const url = new URL(request.url);
      const path = url.pathname;
      if (path === "/api/auth/me") return json(me);
      if (path === "/api/auth/antiforgery") return json({ token: "tok" });
      if (path === "/api/households") return json([{ id: "h1", name: "Home", role: "Adult" }]);
      if (path === "/api/households/h1/recipes/r1") return json(recipe);
      if (path === "/api/households/h1/recipes/r1/scaled") {
        const servings = Number(url.searchParams.get("servings"));
        scaledCalls.push(url.searchParams.get("servings") ?? "");
        if (scaleStatus !== 200) return json({ status: scaleStatus }, scaleStatus);
        // Stands in for the server's Domain scaling (tested in Domain.Tests and Api.Tests).
        return json({
          recipeId: "r1",
          baseServings: 4,
          servings,
          ingredients: [{ ...recipe.ingredients[0]!, quantity: 100 * servings }, recipe.ingredients[1]],
        });
      }
      throw new Error(`unexpected ${request.method} ${path}`);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  Reflect.deleteProperty(navigator, "wakeLock");
  Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
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
          <Route path="/recipes/:id" element={<RecipeDetail />} />
          <Route path="/recipes/:id/cook" element={<RecipeCook />} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

async function cooking(url = "/recipes/r1/cook") {
  renderAt(url);
  await screen.findByTestId("cook-step");
}

const progress = () => screen.getByTestId("cook-progress").textContent;
const stepText = () => screen.getByTestId("cook-step").textContent ?? "";

type FakeSentinel = EventTarget & { release: ReturnType<typeof vi.fn>; released: boolean };

function installWakeLock(request?: () => Promise<WakeLockSentinel>) {
  const sentinels: FakeSentinel[] = [];
  const fn = vi.fn(
    request ??
      (async () => {
        const target = new EventTarget() as FakeSentinel;
        target.released = false;
        target.release = vi.fn(async () => {
          target.released = true;
          target.dispatchEvent(new Event("release"));
        });
        sentinels.push(target);
        return target as unknown as WakeLockSentinel;
      }),
  );
  Object.defineProperty(navigator, "wakeLock", { configurable: true, value: { request: fn } });
  return { request: fn, sentinels };
}

function setVisibility(state: "visible" | "hidden") {
  Object.defineProperty(document, "visibilityState", { configurable: true, value: state });
  document.dispatchEvent(new Event("visibilitychange"));
}

describe("Cooking mode: steps", () => {
  it("shows one step at a time with 'step x of n' and steps through to the done screen and back", async () => {
    const user = userEvent.setup();
    await cooking();
    expect(progress()).toBe(t("cook.stepOf", { current: 1, total: 3 }));
    expect(stepText()).toContain("Kok opp vannet");
    expect(screen.queryByText("Ha i fisken")).toBeNull();
    expect(screen.getByRole("button", { name: t("cook.previous") })).toBeDisabled();

    await user.click(screen.getByRole("button", { name: t("cook.next") }));
    expect(progress()).toBe(t("cook.stepOf", { current: 2, total: 3 }));
    expect(stepText()).toContain("Ha i fisken");
    await user.click(screen.getByRole("button", { name: t("cook.previous") }));
    expect(stepText()).toContain("Kok opp vannet");
    await user.click(screen.getByRole("button", { name: t("cook.next") }));
    await user.click(screen.getByRole("button", { name: t("cook.next") }));
    expect(stepText()).toContain("Server");
    await user.click(screen.getByRole("button", { name: t("cook.finish") }));

    const heading = screen.getByRole("heading", { level: 1, name: t("cook.done") });
    expect(heading).toHaveFocus();
    expect(screen.getByText(t("cook.doneText", { total: 3 }))).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: t("cook.backToSteps") }));
    expect(stepText()).toContain("Server");
    expect(progress()).toBe(t("cook.stepOf", { current: 3, total: 3 }));
  });

  it("announces the step in a polite live region", async () => {
    await cooking();
    const region = screen.getByTestId("cook-step");
    expect(region).toHaveAttribute("aria-live", "polite");
    expect(region).toHaveAttribute("aria-atomic", "true");
  });

  it("changes step with the arrow keys and ignores them in form fields and with modifiers", async () => {
    const user = userEvent.setup();
    await cooking();
    await user.keyboard("{ArrowRight}");
    expect(progress()).toBe(t("cook.stepOf", { current: 2, total: 3 }));
    await user.keyboard("{ArrowRight}{ArrowRight}");
    expect(screen.getByRole("heading", { level: 1, name: t("cook.done") })).toBeInTheDocument();
    await user.keyboard("{ArrowLeft}");
    expect(progress()).toBe(t("cook.stepOf", { current: 3, total: 3 }));
    await user.keyboard("{Alt>}{ArrowLeft}{/Alt}");
    expect(progress()).toBe(t("cook.stepOf", { current: 3, total: 3 }));
    await user.keyboard("{ArrowLeft}{ArrowLeft}{ArrowLeft}{ArrowLeft}");
    expect(progress()).toBe(t("cook.stepOf", { current: 1, total: 3 }));

    const field = document.createElement("input");
    document.body.append(field);
    field.focus();
    await user.keyboard("{ArrowRight}");
    expect(progress()).toBe(t("cook.stepOf", { current: 1, total: 3 }));
    field.remove();
  });

  const swipe = (from: [number, number], to: [number, number]) => {
    const area = screen.getByTestId("cook-step").closest("section")!;
    fireEvent.touchStart(area, { touches: [{ clientX: from[0], clientY: from[1] }] });
    fireEvent.touchEnd(area, { changedTouches: [{ clientX: to[0], clientY: to[1] }] });
  };

  it("swipes left for the next step and right for the previous one; short or mostly vertical swipes do nothing", async () => {
    await cooking();
    swipe([300, 200], [100, 210]);
    expect(progress()).toBe(t("cook.stepOf", { current: 2, total: 3 }));
    swipe([100, 200], [300, 190]);
    expect(progress()).toBe(t("cook.stepOf", { current: 1, total: 3 }));
    swipe([200, 200], [170, 200]); // 30 px: too short
    expect(progress()).toBe(t("cook.stepOf", { current: 1, total: 3 }));
    swipe([300, 100], [200, 400]); // scrolling down, not a swipe
    expect(progress()).toBe(t("cook.stepOf", { current: 1, total: 3 }));
    swipe([100, 200], [300, 200]); // right on the first step stays on it
    expect(progress()).toBe(t("cook.stepOf", { current: 1, total: 3 }));
  });
});

describe("Cooking mode: ingredients and servings", () => {
  it("toggles the ingredients panel and shows stored amounts at the recipe's own servings without a request", async () => {
    const user = userEvent.setup();
    await cooking();
    const toggle = screen.getByRole("button", { name: t("cook.showIngredients") });
    expect(toggle).toHaveAttribute("aria-expanded", "false");
    expect(screen.getByText(/Torsk/)).not.toBeVisible();
    await user.click(toggle);
    expect(screen.getByRole("button", { name: t("cook.hideIngredients") })).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByText(/Torsk/).closest("li")).toHaveTextContent("400 g");
    expect(screen.getByRole("heading", { level: 2, name: t("cook.ingredients", { servings: 4 }) })).toBeInTheDocument();
    expect(scaledCalls).toEqual([]);
  });

  it("asks the API for the new servings and shows what it returns", async () => {
    const user = userEvent.setup();
    await cooking();
    await user.click(screen.getByRole("button", { name: t("cook.showIngredients") }));
    await user.click(screen.getByRole("button", { name: t("recipe.servingsMore") }));
    await user.click(screen.getByRole("button", { name: t("recipe.servingsMore") }));
    expect(screen.getByTestId("servings-value")).toHaveTextContent("6");
    await waitFor(() => expect(screen.getByText(/Torsk/).closest("li")).toHaveTextContent("600 g"));
    expect(scaledCalls).toEqual(["5", "6"]);
    expect(screen.getByText(/Salt/).closest("li")).toHaveTextContent("etter smak");
    await user.click(screen.getByRole("button", { name: t("recipe.servingsFewer") }));
    await user.click(screen.getByRole("button", { name: t("recipe.servingsFewer") }));
    expect(screen.getByText(/Torsk/).closest("li")).toHaveTextContent("400 g"); // back to the stored lines
    expect(scaledCalls).toEqual(["5", "6", "5"]);
  });

  it("starts from ?servings= (the detail page passes its choice) and ignores invalid values", async () => {
    await cooking("/recipes/r1/cook?servings=8");
    expect(screen.getByTestId("servings-value")).toHaveTextContent("8");
    await waitFor(() => expect(scaledCalls).toEqual(["8"]));
  });

  it("falls back to the recipe's servings for an invalid ?servings=", async () => {
    await cooking("/recipes/r1/cook?servings=0");
    expect(screen.getByTestId("servings-value")).toHaveTextContent("4");
    expect(scaledCalls).toEqual([]);
  });

  it("shows a translated error when scaling fails", async () => {
    const user = userEvent.setup();
    scaleStatus = 500;
    await cooking();
    await user.click(screen.getByRole("button", { name: t("cook.showIngredients") }));
    await user.click(screen.getByRole("button", { name: t("recipe.servingsMore") }));
    expect(await screen.findByRole("alert")).toHaveTextContent(t("recipe.scaleFailed"));
  });

  it("is reached from the recipe page with 'Start cooking' and leaves with 'Exit cooking'", async () => {
    const user = userEvent.setup();
    renderAt("/recipes/r1");
    await user.click(await screen.findByRole("link", { name: t("cook.start") }));
    await screen.findByTestId("cook-step");
    expect(screen.getByTestId("url")).toHaveTextContent("/recipes/r1/cook");
    await user.click(screen.getByRole("link", { name: t("cook.exit") }));
    expect(await screen.findByRole("heading", { level: 1, name: "Fiskesuppe" })).toBeInTheDocument();
    expect(screen.getByTestId("url")).toHaveTextContent(/^\/recipes\/r1$/);
  });
});

describe("Cooking mode: screen wake lock", () => {
  it("requests the lock on enter and releases it on exit (unmount)", async () => {
    const lock = installWakeLock();
    const { unmount } = renderAt("/recipes/r1/cook");
    await screen.findByTestId("cook-step");
    await waitFor(() => expect(lock.request).toHaveBeenCalledWith("screen"));
    expect(lock.request).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(lock.sentinels).toHaveLength(1));
    unmount();
    expect(lock.sentinels[0]!.release).toHaveBeenCalledTimes(1);
  });

  it("requests the lock again when the page becomes visible after the browser released it", async () => {
    const lock = installWakeLock();
    await cooking();
    await waitFor(() => expect(lock.sentinels).toHaveLength(1));
    // The browser releases the lock itself when the tab is hidden.
    await act(async () => {
      lock.sentinels[0]!.dispatchEvent(new Event("release"));
      setVisibility("hidden");
    });
    expect(lock.request).toHaveBeenCalledTimes(1);
    await act(async () => setVisibility("visible"));
    await waitFor(() => expect(lock.request).toHaveBeenCalledTimes(2));
  });

  it("does not request twice while it still holds the lock", async () => {
    const lock = installWakeLock();
    await cooking();
    await waitFor(() => expect(lock.sentinels).toHaveLength(1));
    await act(async () => setVisibility("visible"));
    expect(lock.request).toHaveBeenCalledTimes(1);
  });

  it("releases the lock on the done screen and asks again when going back to the steps", async () => {
    const user = userEvent.setup();
    const lock = installWakeLock();
    await cooking();
    await waitFor(() => expect(lock.sentinels).toHaveLength(1));
    await user.keyboard("{ArrowRight}{ArrowRight}{ArrowRight}");
    await screen.findByRole("heading", { level: 1, name: t("cook.done") });
    expect(lock.sentinels[0]!.release).toHaveBeenCalledTimes(1);
    await user.click(screen.getByRole("button", { name: t("cook.backToSteps") }));
    await waitFor(() => expect(lock.request).toHaveBeenCalledTimes(2));
  });

  it("releases a lock that arrives after the page was left", async () => {
    let resolve!: (s: WakeLockSentinel) => void;
    const lock = installWakeLock(() => new Promise<WakeLockSentinel>((r) => (resolve = r)));
    const { unmount } = renderAt("/recipes/r1/cook");
    await screen.findByTestId("cook-step");
    await waitFor(() => expect(lock.request).toHaveBeenCalled());
    unmount();
    const late = { release: vi.fn(async () => undefined), addEventListener: vi.fn() };
    await act(async () => resolve(late as unknown as WakeLockSentinel));
    expect(late.release).toHaveBeenCalledTimes(1);
  });

  it("shows a translated hint and keeps working when the browser has no wake lock", async () => {
    const user = userEvent.setup();
    await cooking();
    expect(screen.getByTestId("wake-lock-hint")).toHaveTextContent(t("cook.wakeLockUnsupported"));
    await user.click(screen.getByRole("button", { name: t("cook.next") }));
    expect(progress()).toBe(t("cook.stepOf", { current: 2, total: 3 }));
  });

  it("shows a hint when the request is refused", async () => {
    installWakeLock(async () => {
      throw new DOMException("denied", "NotAllowedError");
    });
    await cooking();
    expect(await screen.findByTestId("wake-lock-hint")).toHaveTextContent(t("cook.wakeLockFailed"));
  });

  it("shows no hint while the lock is held", async () => {
    const lock = installWakeLock();
    await cooking();
    await waitFor(() => expect(lock.sentinels).toHaveLength(1));
    expect(screen.queryByTestId("wake-lock-hint")).toBeNull();
  });
});
