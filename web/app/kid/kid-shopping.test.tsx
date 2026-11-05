import { act, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { components } from "../api/schema";
import { SessionProvider } from "../session";
import { mondayOf } from "../shopping/week";
import type { HubLike, HubState } from "../shopping/realtime";
import { t } from "../test-i18n";
import { install, newFake, type Fake } from "./fake-server";
import KidApp from "./kid-app";
import { KidShopping } from "./kid-shopping";

type Item = components["schemas"]["ShoppingListItemResponse"];
type List = components["schemas"]["ShoppingListResponse"];

const monday = mondayOf(new Date());

const item = (id: string, name: string, extra: Partial<Item> = {}): Item => ({
  id,
  name,
  isManual: false,
  ingredientId: null,
  section: "Main",
  storeSection: "Other",
  quantities: [],
  recipes: [],
  checked: false,
  checkedByUserId: null,
  checkedByEmail: null,
  checkedAt: null,
  quantityChangedSinceChecked: false,
  alternatives: [],
  isWeekly: false,
  atHome: null,
  ...extra,
});

const listOf = (main: Item[]): List => ({ id: "l1", weekStart: monday, main, checkYouHave: [] });

class FakeHub implements HubLike {
  handlers = new Map<string, (...args: never[]) => void>();
  state: List = listOf([]);
  invoked: string[] = [];
  on(method: string, cb: (...args: never[]) => void) {
    this.handlers.set(method, cb);
  }
  onreconnecting() {}
  onreconnected() {}
  onclose() {}
  async start() {}
  async stop() {}
  async invoke<T>(method: string): Promise<T> {
    this.invoked.push(method);
    if (method === "GetStateIn") return { version: `v${this.invoked.length}`, list: this.state } as HubState as T;
    return undefined as T;
  }
  emit() {
    (this.handlers.get("ShoppingListChanged") as unknown as (e: unknown) => void)({
      kind: "ItemChecked",
      itemId: "i",
      householdId: "h1",
      weekStart: monday,
    });
  }
}

let fake: Fake;
let hub: FakeHub;

beforeEach(() => {
  localStorage.clear();
  fake = newFake();
  fake.kidMode = { profileId: null, householdId: "h1" };
  hub = new FakeHub();
  install(fake);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

const writes = () => fake.calls.filter((c) => !c.startsWith("GET ") && !c.includes("/api/hubs/"));

describe("kid shopping list (T-105)", () => {
  it("shows read-only tiles: name, no controls, checked items marked done", async () => {
    fake.shopping = listOf([
      item("a", "Melk", { storeSection: "Dairy" }),
      item("b", "Brod", { checked: true, storeSection: "DryGoods" }),
    ]);
    hub.state = fake.shopping;
    render(<KidShopping householdId="h1" hubFactory={() => hub} />);
    const tiles = await screen.findAllByTestId("kid-shop-tile");
    expect(tiles.map((x) => x.textContent?.replace(/^\P{L}+/u, ""))).toEqual(["Melk", `Brod${t("kid.shopping.done")}`]);
    expect(within(tiles[1]!).getByTestId("kid-shop-check")).toBeInTheDocument();
    expect(within(tiles[0]!).queryByTestId("kid-shop-check")).not.toBeInTheDocument();
    // nothing to press, type into or tick
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
    expect(screen.queryByRole("textbox")).not.toBeInTheDocument();
    expect(screen.queryByRole("link")).not.toBeInTheDocument();
  });

  it("only reads: no write, no pantry and no weekly-groceries call, even with a check-off queued from a grown-up session", async () => {
    const queued = JSON.stringify([{ householdId: "h1", weekStart: monday, itemId: "a", checked: true }]);
    localStorage.setItem("foodplanner.shopping.queue", queued);
    fake.shopping = listOf([item("a", "Melk")]);
    hub.state = fake.shopping;
    render(<KidShopping householdId="h1" hubFactory={() => hub} />);
    const tile = await screen.findByTestId("kid-shop-tile");
    expect(tile).not.toHaveAttribute("data-checked");
    expect(within(tile).queryByTestId("kid-shop-check")).not.toBeInTheDocument(); // the queue is not applied
    expect(writes()).toEqual([]);
    expect(localStorage.getItem("foodplanner.shopping.queue")).toBe(queued); // the adult's queue is left for the adult
    expect(fake.calls.some((c) => c.includes("pantry") || c.includes("weekly") || c.includes("generate"))).toBe(false);
  });

  it("shows a friendly empty state when no list exists, and does not generate one", async () => {
    hub.state = listOf([]);
    render(<KidShopping householdId="h1" hubFactory={() => hub} />);
    expect(await screen.findByTestId("kid-shopping-empty")).toHaveTextContent(t("kid.shopping.empty"));
    expect(writes()).toEqual([]);
  });

  it("applies a live update from the hub", async () => {
    fake.shopping = listOf([item("a", "Melk")]);
    hub.state = fake.shopping;
    render(<KidShopping householdId="h1" hubFactory={() => hub} />);
    await screen.findByText("Melk");
    hub.state = listOf([item("a", "Melk", { checked: true }), item("c", "Egg")]);
    act(() => hub.emit());
    expect(await screen.findByText("Egg")).toBeInTheDocument();
    await waitFor(() => expect(screen.getAllByTestId("kid-shop-check")).toHaveLength(1));
    expect(writes()).toEqual([]);
  });

  it("is what the cart icon opens in kid mode, and the pantry and other grown-up addresses go back to the dishes", async () => {
    fake.shopping = listOf([item("a", "Melk")]);
    const renderAt = (url: string) =>
      render(
        <MemoryRouter initialEntries={[url]}>
          <SessionProvider>
            <Routes>
              <Route path="*" element={<KidApp />} />
            </Routes>
          </SessionProvider>
        </MemoryRouter>,
      );
    const shopping = renderAt("/shopping");
    expect(await screen.findByTestId("kid-shopping")).toBeInTheDocument();
    expect(await screen.findByText("Melk")).toBeInTheDocument();
    shopping.unmount();
    renderAt("/pantry");
    expect(await screen.findByTestId("kid-home")).toBeInTheDocument();
    expect(fake.calls.some((c) => c.includes("pantry"))).toBe(false);
  });
});
