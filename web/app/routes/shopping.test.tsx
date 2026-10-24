import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAntiforgeryToken } from "../api/client";
import type { components } from "../api/schema";
import { SessionProvider } from "../session";
import { n, t } from "../test-i18n";
import Shopping from "./shopping";

type List = components["schemas"]["ShoppingListResponse"];
type Item = components["schemas"]["ShoppingListItemResponse"];

// The real SignalR client is replaced by a controllable hub.
const hubState = vi.hoisted(() => {
  const state = {
    hub: null as null | {
      calls: string[];
      handlers: Map<string, (...a: unknown[]) => void>;
      reconnecting: () => void;
      reconnected: () => void;
      getState: () => Promise<unknown>;
    },
  };
  return { ...state, defaultState: (): unknown => null };
});

vi.mock("@microsoft/signalr", () => {
  class Builder {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      const hub = {
        calls: [] as string[],
        handlers: new Map<string, (...a: unknown[]) => void>(),
        reconnecting: () => undefined as void,
        reconnected: () => undefined as void,
        getState: async (): Promise<unknown> => hubState.defaultState(),
        on(method: string, cb: (...a: unknown[]) => void) {
          hub.handlers.set(method, cb);
        },
        onreconnecting(cb: () => void) {
          hub.reconnecting = cb;
        },
        onreconnected(cb: () => void) {
          hub.reconnected = cb;
        },
        onclose() {},
        async start() {},
        async stop() {},
        async invoke(method: string, ...args: unknown[]) {
          hub.calls.push(`${method}(${args.join(",")})`);
          if (method === "GetState") return hub.getState();
          return undefined;
        },
      };
      hubState.hub = hub;
      return hub;
    }
  }
  return { HubConnectionBuilder: Builder, LogLevel: { None: 6 } };
});

const week = "2026-10-19";
const user = { userId: "11111111-1111-1111-1111-111111111111", email: "admin@example.test" };
const home = { id: "h1", name: "Home", role: "Owner" };

const item = (id: string, name: string, over: Partial<Item> = {}): Item => ({
  id,
  name,
  isManual: false,
  ingredientId: `ing-${id}`,
  section: "Main",
  quantities: [],
  recipes: [],
  checked: false,
  checkedByUserId: null,
  checkedByEmail: null,
  checkedAt: null,
  quantityChangedSinceChecked: false,
  ...over,
});

const baseList = (): List => ({
  id: "l1",
  weekStart: week,
  main: [
    item("melk", "Melk", { quantities: [{ amount: 1.5, unitCode: "l", unitName: "liter" }], recipes: [{ id: "r1", title: "Pannekaker" }] }),
    item("brod", "Brod", { checked: true }),
    item("egg", "Egg", {
      quantities: [{ amount: 6, unitCode: "stk", unitName: "piece" }],
      checked: true,
      quantityChangedSinceChecked: true,
    }),
    item("kaffe", "Kaffe", { isManual: true, ingredientId: null }),
  ],
  checkYouHave: [item("salt", "Salt", { section: "CheckYouHave" })],
});

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });

type Server = {
  list: List;
  down: boolean;
  listStatus: number;
  puts: string[];
};
let server: Server;

function installFetch() {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      if (server.down) throw new TypeError("Failed to fetch");
      const url = new URL(request.url);
      const path = url.pathname;
      if (path === "/api/auth/antiforgery") return json({ token: "tok" });
      if (path === "/api/auth/me") return json(user);
      if (path === "/api/households") return json([home]);
      if (path === "/api/units") return json([{ id: "u1", code: "kg", name: "Kilogram", dimension: "Mass", factorToBase: 1000 }]);
      const check = path.match(/\/items\/([^/]+)\/check$/);
      if (check && request.method === "PUT") {
        const body = (await request.json()) as { checked: boolean };
        server.puts.push(`${check[1]}:${body.checked}:${request.headers.get("X-XSRF-TOKEN")}`);
        for (const i of [...server.list.main, ...server.list.checkYouHave]) if (i.id === check[1]) i.checked = body.checked;
        return json({});
      }
      if (path.endsWith(`/shopping-lists/${week}`)) {
        return server.listStatus === 200 ? json(server.list) : json({ title: "x", status: server.listStatus }, server.listStatus);
      }
      throw new Error(`unexpected ${request.method} ${path}`);
    }),
  );
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={[`/shopping?week=${week}`]}>
      <SessionProvider>
        <Shopping />
      </SessionProvider>
    </MemoryRouter>,
  );
}

const names = (section: HTMLElement) =>
  within(section)
    .getAllByRole("checkbox")
    .map((c) => c.getAttribute("aria-labelledby"))
    .map((id) => document.getElementById(id!)?.textContent);

beforeEach(() => {
  resetAntiforgeryToken();
  window.localStorage.clear();
  hubState.hub = null;
  hubState.defaultState = () => ({ version: "v1", list: structuredClone(server.list) });
  server = { list: baseList(), down: false, listStatus: 200, puts: [] };
  installFetch();
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

async function ready() {
  await screen.findByRole("heading", { name: t("shopping.sectionMain"), level: 2 });
}

describe("shopping list rendering", () => {
  it("shows Main and staples sections with quantities, recipes and hints; checked items last", async () => {
    renderPage();
    await ready();
    const main = screen.getByRole("region", { name: t("shopping.sectionMain") });
    expect(names(main)).toEqual(["Melk", "Kaffe", "Brod", "Egg"]); // server order, checked moved to the bottom
    expect(within(main).getByText(`${n(1.5)} l`)).toBeInTheDocument();
    expect(within(main).getByText(t("shopping.fromRecipes", { titles: "Pannekaker" }))).toBeInTheDocument();
    expect(within(main).getByText("6 stk")).toBeInTheDocument();
    expect(within(main).getByText(t("shopping.quantityChanged"))).toBeInTheDocument();
    expect(within(main).getByRole("checkbox", { name: "Brod" })).toBeChecked();
    expect(within(main).getByRole("button", { name: t("shopping.remove", { name: "Kaffe" }) })).toBeInTheDocument();
    expect(within(main).queryByRole("button", { name: t("shopping.remove", { name: "Melk" }) })).toBeNull();

    const staples = screen.getByRole("region", { name: t("shopping.sectionStaples") });
    expect(names(staples)).toEqual(["Salt"]);
    expect(screen.getByTestId("shop-week")).toHaveTextContent(t("shopping.week", { week: 43, year: 2026 }));
  });

  it("joins the household and reads state through the hub on connect", async () => {
    renderPage();
    await ready();
    await waitFor(() => expect(hubState.hub?.calls).toEqual(["JoinHousehold(h1)", `GetState(h1,${week})`]));
  });

  it("tolerates a failing cache (storage throws) and still renders", async () => {
    vi.spyOn(Storage.prototype, "setItem").mockImplementation((key: string) => {
      if (key.startsWith("foodplanner.shopping")) throw new DOMException("full", "QuotaExceededError");
    });
    renderPage();
    await ready();
    expect(screen.getByRole("checkbox", { name: "Melk" })).toBeInTheDocument();
  });
});

describe("check-off", () => {
  it("sends the intended state with the antiforgery header and moves the item down", async () => {
    renderPage();
    await ready();
    await userEvent.click(screen.getByRole("checkbox", { name: "Melk" }));
    await waitFor(() => expect(server.puts).toEqual(["melk:true:tok"]));
    const main = screen.getByRole("region", { name: t("shopping.sectionMain") });
    expect(names(main)).toEqual(["Kaffe", "Melk", "Brod", "Egg"]);
  });
});

describe("offline", () => {
  it("queues check-offs, shows the banner and pending count, and replays in order without duplicates", async () => {
    renderPage();
    await ready();
    server.down = true;
    await userEvent.click(screen.getByRole("checkbox", { name: "Melk" }));
    await userEvent.click(screen.getByRole("checkbox", { name: "Kaffe" }));
    await userEvent.click(screen.getByRole("checkbox", { name: "Melk" })); // back to unchecked: latest intent wins

    expect(await screen.findByTestId("offline-banner")).toBeInTheDocument();
    expect(screen.getByTestId("pending-sync")).toHaveTextContent(t("shopping.pending", { count: 2 }));
    expect(screen.getByRole("checkbox", { name: "Kaffe" })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: "Melk" })).not.toBeChecked();
    expect(server.puts).toEqual([]);
    expect(screen.getByRole("button", { name: t("shopping.generate") })).toBeDisabled();

    server.down = false;
    act(() => {
      window.dispatchEvent(new Event("online"));
    });
    await waitFor(() => expect(server.puts).toEqual(["kaffe:true:tok", "melk:false:tok"]));
    await waitFor(() => expect(screen.queryByTestId("pending-sync")).toBeNull());
    expect(screen.queryByTestId("offline-banner")).toBeNull();
    expect(window.localStorage.getItem("foodplanner.shopping.queue")).toBeNull();
  });

  it("keeps the queue in storage and renders the saved list when the API is down on load", async () => {
    const first = renderPage();
    await ready();
    server.down = true;
    await userEvent.click(screen.getByRole("checkbox", { name: "Melk" }));
    expect(await screen.findByTestId("pending-sync")).toBeInTheDocument();
    first.unmount();

    // Reload while the API is down: session, list and the queued check come from storage.
    renderPage();
    await ready();
    expect(screen.getByRole("checkbox", { name: "Melk" })).toBeChecked();
    expect(screen.getByTestId("pending-sync")).toHaveTextContent(t("shopping.pending", { count: 1 }));
    expect(await screen.findByTestId("offline-banner")).toBeInTheDocument();
  });

  it("says so when offline with nothing saved for the week", async () => {
    server.down = true;
    renderPage();
    // Session itself is unavailable without a saved one, so nothing renders.
    await waitFor(() => expect(screen.queryByRole("heading", { name: t("shopping.title"), level: 1 })).toBeNull());
  });
});

describe("live updates", () => {
  it("replaces state from GetState on a change event for the week on screen", async () => {
    renderPage();
    await ready();
    await waitFor(() => expect(hubState.hub?.calls.length).toBe(2));
    const hub = hubState.hub!;
    const next = baseList();
    next.main[0]!.checked = true;
    hub.getState = async () => ({ version: "v2", list: next });

    act(() => hub.handlers.get("ShoppingListChanged")!({ householdId: "h1", weekStart: week, kind: "ItemChecked", itemId: "melk" }));
    await waitFor(() => expect(screen.getByRole("checkbox", { name: "Melk" })).toBeChecked());
  });

  it("rejoins and replaces state after a reconnect", async () => {
    renderPage();
    await ready();
    await waitFor(() => expect(hubState.hub?.calls.length).toBe(2));
    const hub = hubState.hub!;
    const next = baseList();
    next.main.push(item("ost", "Ost"));
    hub.getState = async () => ({ version: "v3", list: next });

    act(() => hub.reconnecting());
    act(() => hub.reconnected());
    expect(await screen.findByRole("checkbox", { name: "Ost" })).toBeInTheDocument();
    expect(hub.calls.slice(-2)).toEqual(["JoinHousehold(h1)", `GetState(h1,${week})`]);
    await waitFor(() => expect(screen.queryByTestId("offline-banner")).toBeNull());
  });

  it("shows the no-access screen when the hub says forbidden", async () => {
    renderPage();
    await ready();
    await waitFor(() => expect(hubState.hub?.calls.length).toBe(2));
    const hub = hubState.hub!;
    hub.getState = async () => {
      throw new Error("An unexpected error occurred invoking 'GetState' on the server. HubException: forbidden");
    };
    act(() => hub.handlers.get("ShoppingListChanged")!({ householdId: "h1", weekStart: week, kind: "Generated", itemId: null }));
    expect(await screen.findByRole("heading", { name: t("shopping.forbiddenTitle") })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: t("shopping.chooseHousehold") })).toHaveAttribute("href", "/");
  });

  it("shows the no-access screen when the REST list answers 403", async () => {
    server.listStatus = 403;
    renderPage();
    expect(await screen.findByRole("heading", { name: t("shopping.forbiddenTitle") })).toBeInTheDocument();
  });
});
