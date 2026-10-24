import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { connectShoppingRealtime, type HubLike, type HubState } from "./realtime";

class FakeHub implements HubLike {
  handlers = new Map<string, (...args: never[]) => void>();
  reconnecting: () => void = () => undefined;
  reconnected: () => void = () => undefined;
  closed: () => void = () => undefined;
  calls: string[] = [];
  startFails = 0;
  forbid = false;
  version = "v1";
  on(method: string, cb: (...args: never[]) => void) {
    this.handlers.set(method, cb);
  }
  onreconnecting(cb: () => void) {
    this.reconnecting = cb;
  }
  onreconnected(cb: () => void) {
    this.reconnected = cb;
  }
  onclose(cb: () => void) {
    this.closed = cb;
  }
  async start() {
    this.calls.push("start");
    if (this.startFails > 0) {
      this.startFails--;
      throw new Error("down");
    }
  }
  async stop() {
    this.calls.push("stop");
  }
  async invoke<T>(method: string, ...args: unknown[]): Promise<T> {
    this.calls.push(`${method}(${args.join(",")})`);
    if (this.forbid && method !== "LeaveHousehold") throw new Error("An unexpected error occurred. HubException: forbidden");
    if (method === "GetState") {
      const state: HubState = {
        version: this.version,
        list: { id: null, weekStart: String(args[1]), main: [], checkYouHave: [] },
      };
      return state as T;
    }
    return undefined as T;
  }
  emit(evt: { householdId: string; weekStart: string }) {
    (this.handlers.get("ShoppingListChanged") as unknown as (e: unknown) => void)({
      kind: "ItemChecked",
      itemId: "i",
      ...evt,
    });
  }
}

function setup(week = "2026-10-19") {
  const hub = new FakeHub();
  const states: HubState[] = [];
  const connection: boolean[] = [];
  const onForbidden = vi.fn();
  const rt = connectShoppingRealtime(
    "h1",
    { getWeek: () => week, onState: (s) => states.push(s), onConnection: (c) => connection.push(c), onForbidden },
    () => hub,
    1000,
  );
  return { hub, states, connection, onForbidden, rt };
}

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

describe("shopping realtime", () => {
  it("connects, joins the household, then reads the state of the week", async () => {
    const { hub, states, connection } = setup();
    await vi.advanceTimersByTimeAsync(0);
    expect(hub.calls).toEqual(["start", "JoinHousehold(h1)", "GetState(h1,2026-10-19)"]);
    expect(connection).toEqual([true]);
    expect(states).toHaveLength(1);
  });

  it("refetches on a change for the week on screen (debounced) and ignores other weeks and households", async () => {
    const { hub, states } = setup();
    await vi.advanceTimersByTimeAsync(0);
    hub.emit({ householdId: "h1", weekStart: "2026-10-26" });
    hub.emit({ householdId: "other", weekStart: "2026-10-19" });
    await vi.advanceTimersByTimeAsync(500);
    expect(states).toHaveLength(1);

    hub.emit({ householdId: "h1", weekStart: "2026-10-19" });
    hub.emit({ householdId: "h1", weekStart: "2026-10-19" });
    hub.emit({ householdId: "h1", weekStart: "2026-10-19" });
    await vi.advanceTimersByTimeAsync(500);
    expect(states).toHaveLength(2); // a burst of three events is one GetState
  });

  it("rejoins and replaces state after a reconnect", async () => {
    const { hub, states, connection } = setup();
    await vi.advanceTimersByTimeAsync(0);
    hub.version = "v2";
    hub.reconnecting();
    hub.reconnected();
    await vi.advanceTimersByTimeAsync(0);
    expect(connection).toEqual([true, false, true]);
    expect(hub.calls.slice(-2)).toEqual(["JoinHousehold(h1)", "GetState(h1,2026-10-19)"]);
    expect(states.map((s) => s.version)).toEqual(["v1", "v2"]);
  });

  it("keeps trying to start when the first connection fails", async () => {
    const hub = new FakeHub();
    hub.startFails = 1;
    const states: HubState[] = [];
    connectShoppingRealtime(
      "h1",
      { getWeek: () => "2026-10-19", onState: (s) => states.push(s), onConnection: () => undefined, onForbidden: vi.fn() },
      () => hub,
      1000,
    );
    await vi.advanceTimersByTimeAsync(0);
    expect(states).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(1000);
    expect(states).toHaveLength(1);
  });

  it("reports forbidden once and stops", async () => {
    const hub = new FakeHub();
    hub.forbid = true;
    const onForbidden = vi.fn();
    const states: HubState[] = [];
    connectShoppingRealtime(
      "h1",
      { getWeek: () => "2026-10-19", onState: (s) => states.push(s), onConnection: () => undefined, onForbidden },
      () => hub,
      1000,
    );
    await vi.advanceTimersByTimeAsync(5000);
    expect(onForbidden).toHaveBeenCalledTimes(1);
    expect(states).toHaveLength(0);
    expect(hub.calls).toContain("stop");
  });

  it("leaves the group and disconnects on stop, and stays quiet afterwards", async () => {
    const { hub, states, rt } = setup();
    await vi.advanceTimersByTimeAsync(0);
    await rt.stop();
    expect(hub.calls.slice(-2)).toEqual(["LeaveHousehold(h1)", "stop"]);
    hub.emit({ householdId: "h1", weekStart: "2026-10-19" });
    hub.closed();
    await vi.advanceTimersByTimeAsync(5000);
    expect(states).toHaveLength(1);
    expect(hub.calls.filter((c) => c === "start")).toHaveLength(1);
  });
});
