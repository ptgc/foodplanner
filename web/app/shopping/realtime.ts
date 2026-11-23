import type { ShoppingList } from "./storage";

/**
 * Client of the ShoppingListHub (docs/domain.md, "Shopping list sync"). The hub is not part of the OpenAPI
 * document, so the payload types below only name the contract; the list inside is the generated REST type.
 */
export type HubState = { version: string; list: ShoppingList };
export type ShoppingListChanged = {
  householdId: string;
  weekStart: string;
  kind: "Generated" | "ManualItemAdded" | "ItemChecked" | "ItemUnchecked" | "ItemRemoved";
  itemId: string | null;
};

export const HUB_PATH = "/api/hubs/shopping-list";

/** The slice of HubConnection used here, so tests can inject a fake. */
export type HubLike = {
  on(method: string, callback: (...args: never[]) => void): void;
  onreconnecting(callback: () => void): void;
  onreconnected(callback: () => void): void;
  onclose(callback: () => void): void;
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke<T>(method: string, ...args: unknown[]): Promise<T>;
};

/**
 * The real connection, with SignalR loaded on the first `start()` (T-128): the library is a lazy chunk, so it is not in
 * the first load of /shopping. Handlers registered before `start()` are attached once it is loaded; `invoke` before a
 * successful start rejects like a disconnected SignalR connection does.
 */
export function defaultHubFactory(): HubLike {
  let hub: HubLike | undefined;
  const pending: Array<(h: HubLike) => void> = [];
  const register = (apply: (h: HubLike) => void) => {
    if (hub) apply(hub);
    else pending.push(apply);
  };
  return {
    on: (method, callback) => register((h) => h.on(method, callback)),
    onreconnecting: (callback) => register((h) => h.onreconnecting(callback)),
    onreconnected: (callback) => register((h) => h.onreconnected(callback)),
    onclose: (callback) => register((h) => h.onclose(callback)),
    async start() {
      if (!hub) {
        const { buildHubConnection } = await import("./signalr-hub");
        const built = (hub ??= buildHubConnection(HUB_PATH));
        pending.splice(0).forEach((apply) => apply(built));
      }
      await hub.start();
    },
    stop: () => (hub ? hub.stop() : Promise.resolve()),
    invoke: <T,>(method: string, ...args: unknown[]) =>
      hub ? hub.invoke<T>(method, ...args) : Promise.reject(new Error("The hub connection is not started.")),
  };
}

export type RealtimeHandlers = {
  /** The week on screen, read at call time (it changes without reconnecting). */
  getWeek: () => string;
  /**
   * The app's language, read at call time. The hub answers in the language it is told per call (a WebSocket cannot
   * carry the app's Accept-Language header, only the browser's), so a language switch needs no new connection (T-078).
   */
  getLocale: () => string;
  /** `locale` is the language the state was asked in, so a slow answer for the language the user just left can be ignored. */
  onState: (state: HubState, locale: string) => void;
  onConnection: (connected: boolean) => void;
  /** Join or GetState said `forbidden`: the user left the household. */
  onForbidden: () => void;
};

export type Realtime = {
  /** Ask the hub for the current state of the week on screen (after a week switch or a local write). */
  refresh: () => Promise<void>;
  stop: () => Promise<void>;
};

const DEBOUNCE_MS = 150;
const RESTART_MS = 5000;

const isForbidden = (e: unknown) => e instanceof Error && e.message.toLowerCase().includes("forbidden");

export function connectShoppingRealtime(
  householdId: string,
  handlers: RealtimeHandlers,
  factory: () => HubLike = defaultHubFactory,
  restartMs = RESTART_MS,
): Realtime {
  const hub = factory();
  let stopped = false;
  let debounce: ReturnType<typeof setTimeout> | undefined;
  let restart: ReturnType<typeof setTimeout> | undefined;

  const fail = (e: unknown) => {
    if (isForbidden(e)) {
      stopped = true;
      handlers.onForbidden();
      void hub.stop().catch(() => undefined);
    }
  };

  const refresh = async () => {
    if (stopped) return;
    try {
      const locale = handlers.getLocale();
      const state = await hub.invoke<HubState>("GetStateIn", householdId, handlers.getWeek(), locale);
      if (!stopped) handlers.onState(state, locale);
    } catch (e) {
      fail(e);
    }
  };

  // Groups do not survive a reconnect, so every (re)connect joins again and then reads the full state.
  const joinAndSync = async () => {
    try {
      await hub.invoke("JoinHousehold", householdId);
    } catch (e) {
      fail(e);
      return;
    }
    await refresh();
  };

  hub.on("ShoppingListChanged", ((evt: ShoppingListChanged) => {
    if (evt.householdId !== householdId || evt.weekStart !== handlers.getWeek()) return;
    clearTimeout(debounce);
    debounce = setTimeout(() => void refresh(), DEBOUNCE_MS);
  }) as (...args: never[]) => void);

  hub.onreconnecting(() => handlers.onConnection(false));
  hub.onreconnected(() => {
    handlers.onConnection(true);
    void joinAndSync();
  });
  hub.onclose(() => {
    if (stopped) return;
    handlers.onConnection(false);
    scheduleStart();
  });

  async function start() {
    if (stopped) return;
    try {
      await hub.start();
    } catch {
      handlers.onConnection(false);
      scheduleStart();
      return;
    }
    if (stopped) {
      void hub.stop().catch(() => undefined);
      return;
    }
    handlers.onConnection(true);
    await joinAndSync();
  }

  function scheduleStart() {
    if (stopped) return;
    clearTimeout(restart);
    restart = setTimeout(() => void start(), restartMs);
  }

  void start();

  return {
    refresh,
    stop: async () => {
      if (stopped) return;
      stopped = true;
      clearTimeout(debounce);
      clearTimeout(restart);
      try {
        await hub.invoke("LeaveHousehold", householdId);
      } catch {
        // Not connected or already gone: stopping the connection leaves the group anyway.
      }
      await hub.stop().catch(() => undefined);
    },
  };
}
