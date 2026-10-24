import type { CheckOp, ShoppingList } from "./storage";

/**
 * Add an intended check state to the queue. Per item only the latest intent matters, so an older op for
 * the same item is dropped and the new one goes to the end: replay order is the order of the final intents.
 */
export function enqueue(queue: CheckOp[], op: CheckOp): CheckOp[] {
  const rest = queue.filter(
    (q) => !(q.householdId === op.householdId && q.weekStart === op.weekStart && q.itemId === op.itemId),
  );
  return [...rest, op];
}

/** Show the queued intents on top of a server list (the optimistic view). */
export function applyQueue(list: ShoppingList, queue: CheckOp[], householdId: string): ShoppingList {
  const mine = queue.filter((q) => q.householdId === householdId && q.weekStart === list.weekStart);
  if (mine.length === 0) return list;
  const wanted = new Map(mine.map((q) => [q.itemId, q.checked]));
  const patch = (items: ShoppingList["main"]) =>
    items.map((i) => {
      const want = wanted.get(i.id);
      return want === undefined || want === i.checked ? i : { ...i, checked: want };
    });
  return { ...list, main: patch(list.main), checkYouHave: patch(list.checkYouHave) };
}

export type SendResult = "ok" | "gone" | "network" | "retry" | "forbidden";

/**
 * Replay ops in order. `ok` and `gone` (item deleted) remove the op; any other result stops and keeps that op
 * and everything after it, so order is preserved for the next attempt.
 */
export async function replay(
  queue: CheckOp[],
  send: (op: CheckOp) => Promise<SendResult>,
): Promise<{ remaining: CheckOp[]; stoppedBy: SendResult | null }> {
  for (let i = 0; i < queue.length; i++) {
    const result = await send(queue[i]!);
    if (result === "ok" || result === "gone") continue;
    return { remaining: queue.slice(i), stoppedBy: result };
  }
  return { remaining: [], stoppedBy: null };
}
