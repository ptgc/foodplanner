import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { api } from "../api/client";
import { useLocale } from "../i18n";
import type { components } from "../api/schema";
import { applyQueue, enqueue, replay, type SendResult } from "./queue";
import {
  connectShoppingRealtime,
  type HubLike,
  type HubState,
  type Realtime,
} from "./realtime";
import {
  readCachedList,
  readQueue,
  writeCachedList,
  writeQueue,
  type CheckOp,
  type ShoppingList,
} from "./storage";

const RETRY_MS = 5000;

export type PantryItem = components["schemas"]["PantryItemResponse"];

/** `locale` is the language the list was asked in: names are catalog data and differ per language (T-078). */
type Server = { week: string; list: ShoppingList; version: string | null; locale: string };
type LoadError = { week: string; kind: "network" | "unauthorized" | "failed" };
type FetchResult = "ok" | "network" | "forbidden" | "unauthorized" | "failed";
export type ActionResult = "ok" | "offline" | "failed";

export type ShoppingListModel = {
  /** The list to show, with queued check-offs applied. Null while nothing (not even a cache) is available. */
  list: ShoppingList | null;
  /** True while the shown list is the saved copy, not something the server confirmed on this visit. */
  fromCache: boolean;
  loading: boolean;
  loadError: LoadError["kind"] | null;
  forbidden: boolean;
  online: boolean;
  /** Check-offs waiting to be sent for the week on screen. */
  pending: number;
  toggle: (itemId: string, currentlyChecked: boolean) => void;
  generate: () => Promise<ActionResult>;
  addItem: (label: string, quantity: number | null, unitId: string | null) => Promise<ActionResult>;
  removeItem: (itemId: string) => Promise<ActionResult>;
  /** The staples the household has confirmed and that have not expired (the "you have N staples" list). */
  pantry: PantryItem[];
  /** "I have it": confirm the staple, then regenerate so it leaves "check you have these" (other sessions follow via the hub). */
  confirmStaple: (ingredientId: string) => Promise<ActionResult>;
  /** "We have this" (T-093): save the ingredient in the pantry with an optional amount (both amount and unit, or neither), then regenerate. */
  haveThis: (ingredientId: string, quantity: number | null, unitId: string | null) => Promise<ActionResult>;
  /** Un-confirm a staple and regenerate so it is prompted again. */
  unconfirmStaple: (ingredientId: string) => Promise<ActionResult>;
  /** How many weekly groceries the household has; null until known (offline, or not read yet). */
  weeklyCount: number | null;
  /** "Add weekly groceries" (T-092): puts the household's weekly set on this week's list. Safe to press again. */
  addWeekly: () => Promise<ActionResult>;
  /** Pick the store being shopped at (null = none, the usual order). The server remembers it per user; the list is re-read in that order (T-087). */
  chooseStore: (storeId: string | null) => Promise<ActionResult>;
};

type Options = { hubFactory?: () => HubLike; restartMs?: number };

/**
 * State of one household's shopping list for the week on screen.
 * Data flow: REST GET (and hub GetState) replace `server`; the saved queue of check-offs is overlaid on it
 * (`applyQueue`) so a tap shows at once and survives a refetch; the queue is replayed in order whenever the
 * API is reachable; every accepted server state is also written to the offline cache.
 */
export function useShoppingList(householdId: string, weekStart: string, options: Options = {}): ShoppingListModel {
  const [server, setServer] = useState<Server | null>(null);
  const [loadError, setLoadError] = useState<LoadError | null>(null);
  const [forbidden, setForbidden] = useState(false);
  const [queue, setQueueState] = useState<CheckOp[]>(() => readQueue());
  const [browserOnline, setBrowserOnline] = useState(() => (typeof navigator === "undefined" ? true : navigator.onLine));
  const [reachable, setReachable] = useState(true);
  const [pantry, setPantry] = useState<PantryItem[]>([]);

  const queueRef = useRef(queue);
  const weekRef = useRef(weekStart);
  const serverRef = useRef<Server | null>(null);
  const rtRef = useRef<Realtime | null>(null);
  const connectedRef = useRef(false);
  const flushing = useRef(false);
  const optionsRef = useRef(options);
  const locale = useLocale();
  const localeRef = useRef(locale);

  useEffect(() => {
    weekRef.current = weekStart;
    localeRef.current = locale;
    optionsRef.current = options;
  });

  const setQueue = useCallback((next: CheckOp[]) => {
    queueRef.current = next;
    setQueueState(next);
    writeQueue(next);
  }, []);

  const base = `/api/households/{householdId}/shopping-lists/{weekStart}` as const;

  const accept = useCallback(
    (list: ShoppingList, version: string | null, askedIn: string) => {
      // A slow answer for a week the user already left must not overwrite the week on screen. Nor may one in the
      // language the user already left (T-078): its names would show in the wrong language.
      if (list.weekStart !== weekRef.current || askedIn !== localeRef.current) return;
      const prev = serverRef.current;
      if (prev && prev.week === list.weekStart && prev.locale === askedIn && version !== null && prev.version === version) {
        setLoadError(null);
        return; // same version: same content, skip the re-render
      }
      const next = { week: list.weekStart, list, version, locale: askedIn };
      serverRef.current = next;
      setServer(next);
      setLoadError(null);
      writeCachedList(householdId, list.weekStart, list, version, askedIn);
    },
    [householdId],
  );

  const fetchRest = useCallback(async (): Promise<FetchResult> => {
    const week = weekRef.current;
    const askedIn = locale; // the api client sends the active language as Accept-Language
    try {
      const { data, response } = await api.GET(base, { params: { path: { householdId, weekStart: week } } });
      setReachable(true);
      if (data) {
        accept(data, null, askedIn);
        return "ok";
      }
      if (response.status === 403) return "forbidden";
      if (response.status === 401) return "unauthorized";
      return "failed";
    } catch {
      setReachable(false);
      return "network";
    }
  }, [accept, base, householdId, locale]);

  /** Re-read the week on screen, through the hub when it is connected (the contract), else REST. */
  const reconcile = useCallback(async () => {
    const rt = rtRef.current;
    if (rt && connectedRef.current) {
      await rt.refresh();
      return;
    }
    const result = await fetchRest();
    if (result === "forbidden") setForbidden(true);
    else if (result !== "ok") {
      const kind = result === "unauthorized" ? "unauthorized" : result === "network" ? "network" : "failed";
      setLoadError({ week: weekRef.current, kind });
    }
  }, [fetchRest]);

  const send = useCallback(
    async (op: CheckOp): Promise<SendResult> => {
      try {
        const { response } = await api.PUT(`${base}/items/{itemId}/check`, {
          params: { path: { householdId: op.householdId, weekStart: op.weekStart, itemId: op.itemId } },
          body: { checked: op.checked },
        });
        setReachable(true);
        if (response.ok) return "ok";
        if (response.status === 403) return "forbidden";
        if (response.status === 401 || response.status === 429 || response.status >= 500) return "retry";
        return "gone"; // 404 (item deleted), 400, 409: retrying cannot help, so drop it
      } catch {
        setReachable(false);
        return "network";
      }
    },
    [base],
  );

  const flush = useCallback(async () => {
    if (flushing.current || queueRef.current.length === 0) return;
    flushing.current = true;
    let sentAny = false;
    try {
      for (;;) {
        const snapshot = queueRef.current;
        if (snapshot.length === 0) break;
        const { remaining, stoppedBy } = await replay(snapshot, send);
        const sent = snapshot.slice(0, snapshot.length - remaining.length);
        sentAny ||= sent.length > 0;
        // Keep ops the user changed or added while we were sending.
        setQueue(
          queueRef.current.filter(
            (q) => !sent.some((s) => s.itemId === q.itemId && s.weekStart === q.weekStart && s.checked === q.checked),
          ),
        );
        if (stoppedBy === "forbidden") setForbidden(true);
        if (stoppedBy) break;
      }
    } finally {
      flushing.current = false;
    }
    if (sentAny) await reconcile();
  }, [reconcile, send, setQueue]);

  // Initial load of a week: flush anything pending first, then read.
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      await flush();
      if (cancelled) return;
      const result = await fetchRest();
      if (cancelled) return;
      if (result === "forbidden") setForbidden(true);
      else if (result !== "ok") {
        setLoadError({ week: weekStart, kind: result === "unauthorized" ? "unauthorized" : result === "network" ? "network" : "failed" });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [householdId, weekStart, flush, fetchRest]);

  // Live connection, one per household; it follows the week on screen through `weekRef`.
  useEffect(() => {
    const rt = connectShoppingRealtime(
      householdId,
      {
        getWeek: () => weekRef.current,
        getLocale: () => localeRef.current,
        onState: (state: HubState, askedIn: string) => {
          setReachable(true);
          accept(state.list, state.version, askedIn);
        },
        onConnection: (connected) => {
          connectedRef.current = connected;
          if (connected) {
            setReachable(true);
            void flush();
          }
        },
        onForbidden: () => setForbidden(true),
      },
      optionsRef.current.hubFactory,
      optionsRef.current.restartMs,
    );
    rtRef.current = rt;
    return () => {
      connectedRef.current = false;
      rtRef.current = null;
      void rt.stop();
    };
  }, [householdId, accept, flush]);

  useEffect(() => {
    const goOnline = () => {
      setBrowserOnline(true);
      setReachable(true);
      void (async () => {
        await flush();
        await reconcile();
      })();
    };
    const goOffline = () => setBrowserOnline(false);
    window.addEventListener("online", goOnline);
    window.addEventListener("offline", goOffline);
    return () => {
      window.removeEventListener("online", goOnline);
      window.removeEventListener("offline", goOffline);
    };
  }, [flush, reconcile]);

  // Waiting changes are retried now and then: the browser's online event is not fired for an API that
  // is down while the network is up, and the page may have been reloaded with a queue from last time.
  const hasQueue = queue.length > 0;
  useEffect(() => {
    if (!hasQueue && reachable) return;
    const timer = setInterval(() => {
      void flush();
      if (!reachable) void reconcile(); // also finds out that the API is back when nothing is queued
    }, RETRY_MS);
    return () => clearInterval(timer);
  }, [hasQueue, reachable, flush, reconcile]);

  const online = browserOnline && reachable;

  const readPantry = useCallback(async (): Promise<PantryItem[] | null> => {
    try {
      const { data } = await api.GET("/api/households/{householdId}/pantry", { params: { path: { householdId } } });
      return data ?? null;
    } catch {
      return null; // offline: keep what we have
    }
  }, [householdId]);

  const fetchPantry = useCallback(async () => {
    const data = await readPantry();
    if (data) setPantry(data);
  }, [readPantry]);

  // Re-read the pantry whenever an accepted server state arrives (a confirmation elsewhere regenerates the list).
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      const data = await readPantry();
      if (!cancelled && data) setPantry(data);
    })();
    return () => {
      cancelled = true;
    };
  }, [readPantry, server]);

  // The size of the weekly set decides whether the "add weekly groceries" button works (T-092). Re-read with every accepted
  // state, so a change on the household page shows when the user comes back.
  const [weeklyCount, setWeeklyCount] = useState<number | null>(null);
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const { data } = await api.GET("/api/households/{householdId}/weekly-groceries", { params: { path: { householdId } } });
        if (!cancelled && data) setWeeklyCount(data.length);
      } catch {
        // offline: keep what we have
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [householdId, server]);

  // A list asked in the other language is not shown (T-078): after a switch the page waits for the list in the new one.
  const current = server && server.week === weekStart && server.locale === locale ? server : null;
  // Derived, not stored: the saved copy (in this language) stands in until the server answers for this week.
  const cached = useMemo(() => (current ? null : readCachedList(householdId, weekStart, locale)), [current, householdId, weekStart, locale]);
  const baseList = current?.list ?? cached?.list ?? null;
  const list = useMemo(() => (baseList ? applyQueue(baseList, queue, householdId) : null), [baseList, queue, householdId]);
  const pending = queue.filter((q) => q.householdId === householdId && q.weekStart === weekStart).length;
  const error = loadError && loadError.week === weekStart ? loadError.kind : null;

  const toggle = useCallback(
    (itemId: string, currentlyChecked: boolean) => {
      setQueue(enqueue(queueRef.current, { householdId, weekStart, itemId, checked: !currentlyChecked }));
      void flush();
    },
    [flush, householdId, setQueue, weekStart],
  );

  const mutate = useCallback(
    async (run: () => Promise<{ ok: boolean; status: number }>): Promise<ActionResult> => {
      if (!online) return "offline";
      try {
        const result = await run();
        setReachable(true);
        if (result.status === 403) {
          setForbidden(true);
          return "failed";
        }
        if (!result.ok) return "failed";
        await reconcile();
        return "ok";
      } catch {
        setReachable(false);
        return "offline";
      }
    },
    [online, reconcile],
  );

  const generate = useCallback(
    () =>
      mutate(async () => {
        const { response } = await api.POST(`${base}/generate`, { params: { path: { householdId, weekStart } } });
        return response;
      }),
    [base, householdId, mutate, weekStart],
  );

  const addWeekly = useCallback(
    () =>
      mutate(async () => {
        const { response } = await api.POST("/api/households/{householdId}/shopping-lists/{weekStart}/weekly", {
          params: { path: { householdId, weekStart } },
        });
        return response;
      }),
    [householdId, mutate, weekStart],
  );

  const addItem = useCallback(
    (label: string, quantity: number | null, unitId: string | null) =>
      mutate(async () => {
        const { response } = await api.POST(`${base}/items`, {
          params: { path: { householdId, weekStart } },
          body: { label, quantity, unitId },
        });
        return response;
      }),
    [base, householdId, mutate, weekStart],
  );

  const removeItem = useCallback(
    (itemId: string) =>
      mutate(async () => {
        const { response } = await api.DELETE(`${base}/items/{itemId}`, {
          params: { path: { householdId, weekStart, itemId } },
        });
        return response;
      }),
    [base, householdId, mutate, weekStart],
  );

  const pantryThenGenerate = useCallback(
    (run: () => Promise<{ ok: boolean; status: number }>) =>
      mutate(async () => {
        const first = await run();
        if (!first.ok) return first;
        const { response } = await api.POST(`${base}/generate`, { params: { path: { householdId, weekStart } } });
        void fetchPantry();
        return response;
      }),
    [base, fetchPantry, householdId, mutate, weekStart],
  );

  const haveThis = useCallback(
    (ingredientId: string, quantity: number | null, unitId: string | null) =>
      pantryThenGenerate(async () => {
        const { response } = await api.PUT("/api/households/{householdId}/pantry/{ingredientId}", {
          params: { path: { householdId, ingredientId } },
          body: { expiresAt: null, quantity, unitId },
        });
        return response;
      }),
    [householdId, pantryThenGenerate],
  );

  const confirmStaple = useCallback((ingredientId: string) => haveThis(ingredientId, null, null), [haveThis]);

  const unconfirmStaple = useCallback(
    (ingredientId: string) =>
      pantryThenGenerate(async () => {
        const { response } = await api.DELETE("/api/households/{householdId}/pantry/{ingredientId}", {
          params: { path: { householdId, ingredientId } },
        });
        return response;
      }),
    [householdId, pantryThenGenerate],
  );

  // Remembered per user and household on the server (T-087); the list comes back in the new store's order.
  const chooseStore = useCallback(
    (storeId: string | null) =>
      mutate(async () => {
        const { response } = await api.PUT("/api/households/{householdId}/chosen-store", {
          params: { path: { householdId } },
          body: { storeId },
        });
        return response;
      }),
    [householdId, mutate],
  );

  return {
    chooseStore,
    list,
    fromCache: !current && !!cached,
    loading: !list && !error && !forbidden,
    loadError: error,
    forbidden,
    online,
    pending,
    toggle,
    generate,
    addItem,
    removeItem,
    pantry,
    confirmStaple,
    haveThis,
    unconfirmStaple,
    weeklyCount,
    addWeekly,
  };
}
