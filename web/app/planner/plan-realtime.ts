import { defaultHubFactory, type HubLike } from "../shopping/realtime";

/**
 * Listens for `MealPlanChanged` on the household hub (T-026). The event carries no plan content (kind, week, entry id
 * only), so the screen simply reads the week again. Same hub and join contract as the shopping list (docs/domain.md).
 */
export type MealPlanChanged = {
  householdId: string;
  weekStart: string;
  kind: "EntryAdded" | "EntryChanged" | "EntryRemoved" | "Locked" | "Unlocked";
  entryId: string | null;
};

export type PlanRealtimeHandlers = {
  /** The week on screen, read at call time (it changes without reconnecting). */
  getWeek: () => string;
  /** Something changed in the week on screen, or the connection came back and may have missed changes. */
  onChange: () => void;
};

const DEBOUNCE_MS = 150;
const RESTART_MS = 5000;

export function connectPlanRealtime(
  householdId: string,
  handlers: PlanRealtimeHandlers,
  factory: () => HubLike = defaultHubFactory,
  restartMs = RESTART_MS,
): { stop: () => Promise<void> } {
  const hub = factory();
  let stopped = false;
  let debounce: ReturnType<typeof setTimeout> | undefined;
  let restart: ReturnType<typeof setTimeout> | undefined;

  const join = async (missedSomething: boolean) => {
    try {
      await hub.invoke("JoinHousehold", householdId);
    } catch (e) {
      // "forbidden": the person left the household or kid mode now excludes it; nothing to listen to.
      if (e instanceof Error && e.message.toLowerCase().includes("forbidden")) {
        stopped = true;
        void hub.stop().catch(() => undefined);
      }
      return;
    }
    if (missedSomething && !stopped) handlers.onChange();
  };

  hub.on("MealPlanChanged", ((evt: MealPlanChanged) => {
    if (evt.householdId !== householdId || evt.weekStart !== handlers.getWeek()) return;
    clearTimeout(debounce);
    debounce = setTimeout(() => handlers.onChange(), DEBOUNCE_MS);
  }) as (...args: never[]) => void);

  // Groups do not survive a reconnect, so every reconnect joins again (and reads the week, in case a change was missed).
  hub.onreconnected(() => void join(true));
  hub.onreconnecting(() => undefined);
  hub.onclose(() => {
    if (!stopped) scheduleStart();
  });

  async function start() {
    if (stopped) return;
    try {
      await hub.start();
    } catch {
      scheduleStart();
      return;
    }
    if (stopped) {
      void hub.stop().catch(() => undefined);
      return;
    }
    await join(false);
  }

  function scheduleStart() {
    if (stopped) return;
    clearTimeout(restart);
    restart = setTimeout(() => void start(), restartMs);
  }

  void start();

  return {
    stop: async () => {
      if (stopped) return;
      stopped = true;
      clearTimeout(debounce);
      clearTimeout(restart);
      try {
        await hub.invoke("LeaveHousehold", householdId);
      } catch {
        // Not connected: stopping the connection leaves the group anyway.
      }
      await hub.stop().catch(() => undefined);
    },
  };
}
