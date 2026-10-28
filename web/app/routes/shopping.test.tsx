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
  storeSection: "Other",
  quantities: [],
  recipes: [],
  checked: false,
  checkedByUserId: null,
  checkedByEmail: null,
  checkedAt: null,
  quantityChangedSinceChecked: false,
  alternatives: [],
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
  checkYouHave: [
    item("salt", "Salt", {
      section: "CheckYouHave",
      alternatives: [{ ingredientId: "marg", ingredientName: "Margarin" }, { ingredientId: "olje", ingredientName: "Olje" }],
    }),
  ],
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
  /** The household's pantry; a confirm moves the staple out of the list on the next generate, like the API. */
  pantry: { ingredientId: string; name: string }[];
  pantryCalls: string[];
  /** Export requests the page made: path + query + Accept-Language. */
  exports: string[];
  exportStatus: number;
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
      if (path.endsWith(`/shopping-lists/${week}/export`)) {
        server.exports.push(`${path}${url.search} ${request.headers.get("Accept-Language")}`);
        return server.exportStatus === 200
          ? json({ format: "text", title: "Shopping list, week 44", text: "- Melk 1.5 l\n- Kaffe", url: null })
          : json({ title: "x", status: server.exportStatus }, server.exportStatus);
      }
      const pantryPath = path.match(/\/pantry(?:\/([^/]+))?$/);
      if (pantryPath) {
        const id = pantryPath[1];
        if (request.method === "GET") {
          return json(server.pantry.map((p) => ({ ...p, confirmedAt: "2026-10-25T10:00:00Z", confirmedByUserId: user.userId, expiresAt: null })));
        }
        server.pantryCalls.push(`${request.method} ${id}:${request.headers.get("X-XSRF-TOKEN")}`);
        if (request.method === "PUT") {
          const staple = server.list.checkYouHave.find((i) => i.ingredientId === id);
          server.pantry.push({ ingredientId: id!, name: staple?.name ?? id! });
          return json({});
        }
        server.pantry = server.pantry.filter((p) => p.ingredientId !== id);
        return new Response(null, { status: 204 });
      }
      if (path.endsWith(`/shopping-lists/${week}/generate`) && request.method === "POST") {
        server.pantryCalls.push("POST generate");
        const have = new Set(server.pantry.map((p) => p.ingredientId));
        server.list = { ...server.list, checkYouHave: server.list.checkYouHave.filter((i) => !have.has(i.ingredientId!)) };
        return json(server.list);
      }
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
  server = { list: baseList(), down: false, listStatus: 200, puts: [], pantry: [], pantryCalls: [], exports: [], exportStatus: 200 };
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
    // Alternatives of a check-you-have item read "or margarin, olje" (T-076).
    expect(screen.getByTestId("shop-alternatives")).toHaveTextContent(t("shopping.or", { names: "Margarin, Olje" }));
    expect(within(main).getByText(t("shopping.quantityChanged"))).toBeInTheDocument();
    expect(within(main).getByRole("checkbox", { name: "Brod" })).toBeChecked();
    expect(within(main).getByRole("button", { name: t("shopping.remove", { name: "Kaffe" }) })).toBeInTheDocument();
    expect(within(main).queryByRole("button", { name: t("shopping.remove", { name: "Melk" }) })).toBeNull();

    const staples = screen.getByRole("region", { name: t("shopping.sectionStaples") });
    expect(names(staples)).toEqual(["Salt"]);
    expect(screen.getByTestId("shop-week")).toHaveTextContent(t("shopping.week", { week: 43, year: 2026 }));
  });

  it("groups the to-buy list under store section headings in the server's order, and checks off inside a group", async () => {
    server.list.main = [
      item("tomat", "Tomat", { storeSection: "FruitAndVegetables" }),
      item("agurk", "Agurk", { storeSection: "FruitAndVegetables", checked: true }),
      item("melk", "Melk", { storeSection: "Dairy" }),
      item("ris", "Ris", { storeSection: "DryGoods" }),
      item("kaffe", "Kaffe", { isManual: true, ingredientId: null }),
    ];
    renderPage();
    await ready();

    const groups = screen.getAllByTestId("shop-group");
    expect(groups.map((g) => within(g).getByRole("heading", { level: 3 }).textContent)).toEqual([
      t("storeSection.FruitAndVegetables"),
      t("storeSection.Dairy"),
      t("storeSection.DryGoods"),
      t("storeSection.Other"),
    ]);
    expect(names(groups[0]!)).toEqual(["Tomat", "Agurk"]); // checked last inside the group
    expect(names(groups[1]!)).toEqual(["Melk"]);

    await userEvent.click(screen.getByRole("checkbox", { name: "Melk" }));
    await waitFor(() => expect(server.puts.length).toBe(1));
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

describe("pantry: I have it", () => {
  it("shows a 44px+ button only on check-you-have items, and one tap confirms, regenerates and lists the staple as at home", async () => {
    renderPage();
    await ready();
    const main = screen.getByRole("region", { name: t("shopping.sectionMain") });
    expect(within(main).queryByRole("button", { name: t("shopping.haveItFor", { name: "Melk" }) })).toBeNull();
    expect(screen.queryByTestId("pantry")).toBeNull();

    const button = screen.getByRole("button", { name: t("shopping.haveItFor", { name: "Salt" }) });
    expect(button).toHaveTextContent(t("shopping.haveIt"));
    expect(button).toHaveClass("shop-have"); // min-height/min-width: var(--touch) = 44px in app.css
    await userEvent.click(button);

    await waitFor(() => expect(server.pantryCalls).toEqual(["PUT ing-salt:tok", "POST generate"]));
    await waitFor(() => expect(screen.queryByRole("region", { name: t("shopping.sectionStaples") })).toBeNull());
    const pantry = await screen.findByTestId("pantry");
    expect(pantry).toHaveTextContent(t("shopping.pantry", { count: 1 }));
    expect(within(pantry).getByText("Salt")).toBeInTheDocument();
  });

  it("un-confirms from the pantry summary and regenerates so the staple prompts again", async () => {
    server.pantry = [{ ingredientId: "ing-salt", name: "Salt" }];
    server.list = { ...server.list, checkYouHave: [] };
    renderPage();
    await ready();
    const pantry = await screen.findByTestId("pantry");

    await userEvent.click(within(pantry).getByRole("button", { name: t("shopping.pantryRemoveFor", { name: "Salt" }) }));

    await waitFor(() => expect(server.pantryCalls).toEqual(["DELETE ing-salt:tok", "POST generate"]));
    await waitFor(() => expect(screen.queryByTestId("pantry")).toBeNull());
  });

  it("disables the button while offline", async () => {
    renderPage();
    await ready();
    server.down = true;
    await userEvent.click(screen.getByRole("checkbox", { name: "Melk" })); // makes the app notice it is offline
    await screen.findByTestId("offline-banner");

    expect(screen.getByRole("button", { name: t("shopping.haveItFor", { name: "Salt" }) })).toBeDisabled();
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

  it("does not render a list cached by an older app version (no alternatives or storeSection), and keeps the queue", async () => {
    // Shape written before T-076/T-085: items lack alternatives and storeSection; no schema version.
    const old = { id: null, weekStart: week, main: [{ id: "melk", name: "Melk", isManual: false, section: "Main", quantities: [], recipes: [], checked: false }], checkYouHave: [] };
    window.localStorage.setItem(`foodplanner.shopping.list.h1.${week}`, JSON.stringify({ version: "v0", list: old, savedAt: "2026-10-01T00:00:00Z" }));
    window.localStorage.setItem("foodplanner.shopping.queue", JSON.stringify([{ householdId: "h1", weekStart: week, itemId: "melk", checked: true }]));
    server.down = true;
    hubState.defaultState = () => {
      throw new Error("down");
    };
    window.localStorage.setItem("foodplanner.session", JSON.stringify({ user, households: [home] }));
    renderPage();
    expect(await screen.findByTestId("offline-banner")).toBeInTheDocument();
    expect(screen.queryByTestId("shop-item")).toBeNull(); // dropped, not rendered raw and not crashed
    expect(window.localStorage.getItem(`foodplanner.shopping.list.h1.${week}`)).toBeNull();
    expect(JSON.parse(window.localStorage.getItem("foodplanner.shopping.queue")!)).toHaveLength(1);
  });

  it("renders a server item that lacks the optional arrays instead of crashing", async () => {
    const bare = { id: "melk", name: "Melk", isManual: false, section: "Main", storeSection: "Dairy", checked: false, checkedByUserId: null, checkedByEmail: null, checkedAt: null, quantityChangedSinceChecked: false, ingredientId: null };
    server.list.main = [bare as unknown as Item];
    renderPage();
    await ready();
    expect(screen.getByRole("checkbox", { name: "Melk" })).toBeInTheDocument();
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

describe("export (share and copy)", () => {
  const payload = { title: "Shopping list, week 44", text: "- Melk 1.5 l\n- Kaffe" };
  let writeText: ReturnType<typeof vi.fn>;

  const setNav = (props: Record<string, unknown>) => {
    for (const [k, v] of Object.entries(props)) Object.defineProperty(navigator, k, { value: v, configurable: true });
  };
  beforeEach(() => {
    writeText = vi.fn(async () => undefined);
    setNav({ clipboard: { writeText }, share: undefined, canShare: undefined });
  });
  afterEach(() => {
    setNav({ clipboard: undefined, share: undefined, canShare: undefined });
    Reflect.deleteProperty(document, "execCommand");
  });

  const button = (key: "shopping.share" | "shopping.copy") => screen.getByRole("button", { name: t(key) });

  it("calls the export endpoint for the week on screen with Accept-Language", async () => {
    renderPage();
    await ready();
    await userEvent.click(button("shopping.copy"));
    await waitFor(() => expect(server.exports).toHaveLength(1));
    expect(server.exports[0]).toMatch(new RegExp(`^/api/households/h1/shopping-lists/${week}/export\\?format=text (nb|en)$`));
    expect(button("shopping.share")).toHaveClass("button"); // .button carries the 44px minimum (app.css --touch)
  });

  it("copies the text and announces it in a polite live region", async () => {
    renderPage();
    await ready();
    await userEvent.click(button("shopping.copy"));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(payload.text));
    const toast = screen.getByTestId("copy-toast");
    expect(toast).toHaveAttribute("aria-live", "polite");
    await waitFor(() => expect(toast).toHaveTextContent(t("shopping.copied")));
  });

  it("shares title and text when Web Share is available", async () => {
    const share = vi.fn(async () => undefined);
    setNav({ share, canShare: vi.fn(() => true) });
    renderPage();
    await ready();
    await userEvent.click(button("shopping.share"));
    await waitFor(() => expect(share).toHaveBeenCalledWith(payload));
    expect(writeText).not.toHaveBeenCalled();
  });

  it("copies instead when Web Share is unavailable", async () => {
    renderPage();
    await ready();
    await userEvent.click(button("shopping.share"));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(payload.text));
    await waitFor(() => expect(screen.getByTestId("copy-toast")).toHaveTextContent(t("shopping.copied")));
  });

  it("copies instead when canShare says no", async () => {
    const share = vi.fn(async () => undefined);
    setNav({ share, canShare: vi.fn(() => false) });
    renderPage();
    await ready();
    await userEvent.click(button("shopping.share"));
    await waitFor(() => expect(writeText).toHaveBeenCalled());
    expect(share).not.toHaveBeenCalled();
  });

  it("does nothing more when the user cancels the share sheet", async () => {
    const share = vi.fn(async () => {
      throw new DOMException("cancelled", "AbortError");
    });
    setNav({ share });
    renderPage();
    await ready();
    await userEvent.click(button("shopping.share"));
    await waitFor(() => expect(share).toHaveBeenCalled());
    await waitFor(() => expect(button("shopping.share")).toBeEnabled());
    expect(writeText).not.toHaveBeenCalled();
    expect(screen.getByTestId("copy-toast")).toBeEmptyDOMElement();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("falls back to copy when sharing fails for another reason", async () => {
    const share = vi.fn(async () => {
      throw new DOMException("no", "NotAllowedError");
    });
    setNav({ share });
    renderPage();
    await ready();
    await userEvent.click(button("shopping.share"));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(payload.text));
  });

  it("falls back to execCommand when the async clipboard is missing", async () => {
    setNav({ clipboard: undefined });
    const exec = vi.fn(() => true);
    Object.defineProperty(document, "execCommand", { value: exec, configurable: true });
    renderPage();
    await ready();
    await userEvent.click(button("shopping.copy"));
    await waitFor(() => expect(exec).toHaveBeenCalledWith("copy"));
    await waitFor(() => expect(screen.getByTestId("copy-toast")).toHaveTextContent(t("shopping.copied")));
    expect(document.querySelector("textarea[aria-hidden]")).toBeNull(); // the helper is removed again
  });

  it("shows the text to copy by hand when both clipboard paths fail", async () => {
    writeText.mockRejectedValue(new DOMException("denied", "NotAllowedError"));
    Object.defineProperty(document, "execCommand", { value: vi.fn(() => false), configurable: true });
    renderPage();
    await ready();
    await userEvent.click(button("shopping.copy"));
    const area = await screen.findByLabelText(t("shopping.copyManually"));
    expect(area).toHaveValue(payload.text);
    expect(screen.getByTestId("copy-toast")).toBeEmptyDOMElement();
  });

  it("shows an error when the export request fails", async () => {
    server.exportStatus = 500;
    renderPage();
    await ready();
    await userEvent.click(button("shopping.copy"));
    expect(await screen.findByRole("alert")).toHaveTextContent(t("shopping.exportFailed"));
    expect(writeText).not.toHaveBeenCalled();
  });

  it("is disabled with a hint while offline", async () => {
    renderPage();
    await ready();
    server.down = true;
    await userEvent.click(screen.getByRole("checkbox", { name: "Melk" })); // makes the app notice it is offline
    await screen.findByTestId("offline-banner");
    expect(button("shopping.share")).toBeDisabled();
    expect(button("shopping.copy")).toBeDisabled();
    expect(screen.getByText(t("shopping.exportNeedsOnline"))).toBeInTheDocument();
  });
});
