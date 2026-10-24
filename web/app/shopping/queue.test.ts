import { describe, expect, it, vi } from "vitest";
import { applyQueue, enqueue, replay } from "./queue";
import type { CheckOp, ShoppingList } from "./storage";

const op = (itemId: string, checked: boolean, weekStart = "2026-10-19"): CheckOp => ({
  householdId: "h1",
  weekStart,
  itemId,
  checked,
});

const item = (id: string, checked = false) => ({
  id,
  name: id,
  isManual: false,
  ingredientId: null,
  section: "Main" as const,
  quantities: [],
  recipes: [],
  checked,
  checkedByUserId: null,
  checkedByEmail: null,
  checkedAt: null,
  quantityChangedSinceChecked: false,
});

const list = (): ShoppingList => ({
  id: "l1",
  weekStart: "2026-10-19",
  main: [item("a"), item("b", true)],
  checkYouHave: [item("s")],
});

describe("enqueue", () => {
  it("keeps one op per item: the latest intent wins and moves to the end", () => {
    let q: CheckOp[] = [];
    q = enqueue(q, op("a", true));
    q = enqueue(q, op("b", true));
    q = enqueue(q, op("a", false));
    expect(q).toEqual([op("b", true), op("a", false)]);
  });

  it("does not merge the same item id in different weeks", () => {
    const q = enqueue([op("a", true, "2026-10-12")], op("a", true));
    expect(q).toHaveLength(2);
  });
});

describe("applyQueue", () => {
  it("overlays queued intents on both sections", () => {
    const view = applyQueue(list(), [op("a", true), op("b", false), op("s", true)], "h1");
    expect(view.main.map((i) => i.checked)).toEqual([true, false]);
    expect(view.checkYouHave[0]?.checked).toBe(true);
  });

  it("ignores other households and weeks and returns the same object when nothing applies", () => {
    const l = list();
    expect(applyQueue(l, [op("a", true, "2026-10-12")], "h1")).toBe(l);
    expect(applyQueue(l, [op("a", true)], "other")).toBe(l);
  });
});

describe("replay", () => {
  it("sends in order and empties the queue", async () => {
    const sent: string[] = [];
    const result = await replay([op("a", true), op("b", false), op("c", true)], async (o) => {
      sent.push(`${o.itemId}:${o.checked}`);
      return "ok";
    });
    expect(sent).toEqual(["a:true", "b:false", "c:true"]);
    expect(result).toEqual({ remaining: [], stoppedBy: null });
  });

  it("drops ops whose item is gone but stops at a network failure, keeping order", async () => {
    const send = vi
      .fn<(o: CheckOp) => Promise<"ok" | "gone" | "network">>()
      .mockResolvedValueOnce("gone")
      .mockResolvedValueOnce("network");
    const result = await replay([op("a", true), op("b", true), op("c", true)], send);
    expect(send).toHaveBeenCalledTimes(2);
    expect(result.remaining.map((o) => o.itemId)).toEqual(["b", "c"]);
    expect(result.stoppedBy).toBe("network");
  });
});
