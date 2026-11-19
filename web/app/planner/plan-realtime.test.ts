import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { HubLike } from "../shopping/realtime";
import { connectPlanRealtime, type MealPlanChanged } from "./plan-realtime";

class FakeHub implements HubLike {
  handlers = new Map<string, (...args: never[]) => void>();
  reconnected: () => void = () => undefined;
  closed: () => void = () => undefined;
  calls: string[] = [];
  startFails = 0;
  forbid = false;
  on(method: string, cb: (...args: never[]) => void) {
    this.handlers.set(method, cb);
  }
  onreconnecting() {}
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
    if (this.forbid && method !== "LeaveHousehold") throw new Error("HubException: forbidden");
    return undefined as T;
  }
  emit(evt: Partial<MealPlanChanged>) {
    (this.handlers.get("MealPlanChanged") as unknown as (e: unknown) => void)({
      householdId: "h1",
      weekStart: "2031-03-03",
      kind: "EntryAdded",
      entryId: "e1",
      ...evt,
    });
  }
}

function setup(week = "2031-03-03") {
  const hub = new FakeHub();
  const onChange = vi.fn();
  const rt = connectPlanRealtime("h1", { getWeek: () => week, onChange }, () => hub, 1000);
  return { hub, onChange, rt };
}

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

describe("plan realtime (MealPlanChanged)", () => {
  it("connects and joins the household group", async () => {
    const { hub } = setup();
    await vi.advanceTimersByTimeAsync(0);
    expect(hub.calls).toEqual(["start", "JoinHousehold(h1)"]);
  });

  it("asks for a re-read once for a burst of changes to the week on screen (all kinds, lock included)", async () => {
    const { hub, onChange } = setup();
    await vi.advanceTimersByTimeAsync(0);
    hub.emit({ kind: "EntryAdded" });
    hub.emit({ kind: "Locked", entryId: null });
    hub.emit({ kind: "Unlocked", entryId: null });
    await vi.advanceTimersByTimeAsync(200);
    expect(onChange).toHaveBeenCalledTimes(1);
  });

  it("ignores another week and another household", async () => {
    const { hub, onChange } = setup();
    await vi.advanceTimersByTimeAsync(0);
    hub.emit({ weekStart: "2031-03-10" });
    hub.emit({ householdId: "h2" });
    await vi.advanceTimersByTimeAsync(500);
    expect(onChange).not.toHaveBeenCalled();
  });

  it("joins again and re-reads after a reconnect (changes may have been missed)", async () => {
    const { hub, onChange } = setup();
    await vi.advanceTimersByTimeAsync(0);
    hub.reconnected();
    await vi.advanceTimersByTimeAsync(0);
    expect(hub.calls.filter((c) => c === "JoinHousehold(h1)")).toHaveLength(2);
    expect(onChange).toHaveBeenCalledTimes(1);
  });

  it("retries a failed start", async () => {
    const hub = new FakeHub();
    hub.startFails = 1;
    connectPlanRealtime("h1", { getWeek: () => "w", onChange: vi.fn() }, () => hub, 1000);
    await vi.advanceTimersByTimeAsync(0);
    expect(hub.calls).toEqual(["start"]);
    await vi.advanceTimersByTimeAsync(1000);
    expect(hub.calls).toEqual(["start", "start", "JoinHousehold(h1)"]);
  });

  it("gives up quietly when the join is forbidden", async () => {
    const hub = new FakeHub();
    hub.forbid = true;
    const onChange = vi.fn();
    connectPlanRealtime("h1", { getWeek: () => "2031-03-03", onChange }, () => hub, 1000);
    await vi.advanceTimersByTimeAsync(0);
    expect(hub.calls).toContain("stop");
    hub.closed();
    await vi.advanceTimersByTimeAsync(5000);
    expect(hub.calls.filter((c) => c === "start")).toHaveLength(1);
  });

  it("leaves the group and stops on stop()", async () => {
    const { hub, rt } = setup();
    await vi.advanceTimersByTimeAsync(0);
    await rt.stop();
    expect(hub.calls.slice(-2)).toEqual(["LeaveHousehold(h1)", "stop"]);
  });
});
