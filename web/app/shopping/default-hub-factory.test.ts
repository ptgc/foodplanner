import { describe, expect, it, vi } from "vitest";

// T-128 verification: the lazy SignalR wrapper must queue handlers until start() and reject invoke before it.
const built = vi.hoisted(() => ({ hub: undefined as undefined | Record<string, ReturnType<typeof vi.fn>> }));

vi.mock("./signalr-hub", () => ({
  buildHubConnection: () => {
    built.hub = {
      on: vi.fn(),
      onreconnecting: vi.fn(),
      onreconnected: vi.fn(),
      onclose: vi.fn(),
      start: vi.fn().mockResolvedValue(undefined),
      stop: vi.fn().mockResolvedValue(undefined),
      invoke: vi.fn().mockResolvedValue("ok"),
    };
    return built.hub;
  },
}));

import { defaultHubFactory } from "./realtime";

describe("defaultHubFactory (lazy SignalR)", () => {
  it("queues handlers until start, attaches all of them, and rejects invoke before start", async () => {
    const hub = defaultHubFactory();
    const cb = () => undefined;
    hub.on("ShoppingListChanged", cb);
    hub.onreconnecting(cb);
    hub.onreconnected(cb);
    hub.onclose(cb);

    await expect(hub.invoke("X")).rejects.toThrow();
    await expect(hub.stop()).resolves.toBeUndefined();
    expect(built.hub).toBeUndefined();

    await hub.start();
    expect(built.hub!.on).toHaveBeenCalledWith("ShoppingListChanged", cb);
    expect(built.hub!.onreconnecting).toHaveBeenCalledWith(cb);
    expect(built.hub!.onreconnected).toHaveBeenCalledWith(cb);
    expect(built.hub!.onclose).toHaveBeenCalledWith(cb);
    expect(built.hub!.start).toHaveBeenCalledTimes(1);

    await expect(hub.invoke("X", 1)).resolves.toBe("ok");
    const later = () => undefined;
    hub.onclose(later); // after start: attached directly
    expect(built.hub!.onclose).toHaveBeenCalledWith(later);

    await hub.start(); // restart reuses the same connection, handlers not re-attached
    expect(built.hub!.start).toHaveBeenCalledTimes(2);
    expect(built.hub!.on).toHaveBeenCalledTimes(1);
  });

  it("concurrent start calls build one connection", async () => {
    built.hub = undefined;
    const hub = defaultHubFactory();
    hub.on("A", () => undefined);
    await Promise.all([hub.start(), hub.start()]);
    expect(built.hub!.on).toHaveBeenCalledTimes(1);
  });
});
