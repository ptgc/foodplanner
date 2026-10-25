import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { t } from "../test-i18n";
import { UpdateBanner } from "./update-banner";
import type { SwContainer } from "./use-service-worker";

// A tiny fake of navigator.serviceWorker: `controller` is the running worker (null on the very first visit).
class FakeWorker extends EventTarget {
  state: ServiceWorkerState = "installing";
  postMessage = vi.fn();
  setState(state: ServiceWorkerState) {
    this.state = state;
    this.dispatchEvent(new Event("statechange"));
  }
}

class FakeRegistration extends EventTarget {
  waiting: FakeWorker | null = null;
  installing: FakeWorker | null = null;
  update = vi.fn(async () => undefined);
}

class FakeContainer extends EventTarget {
  controller: object | null;
  registration = new FakeRegistration();
  register = vi.fn(async () => this.registration);
  constructor(controlled: boolean) {
    super();
    this.controller = controlled ? {} : null;
  }
}

const asContainer = (c: FakeContainer) => c as unknown as SwContainer & EventTarget;

async function flush() {
  await act(async () => {
    await Promise.resolve();
  });
}

describe("UpdateBanner", () => {
  it("registers /sw.js and shows nothing while there is no update", async () => {
    const sw = new FakeContainer(true);
    render(<UpdateBanner container={asContainer(sw)} enabled />);
    await flush();
    expect(sw.register).toHaveBeenCalledWith("/sw.js", { scope: "/" });
    expect(screen.queryByTestId("update-banner")).not.toBeInTheDocument();
  });

  it("does not register in the dev server (enabled=false)", async () => {
    const sw = new FakeContainer(true);
    render(<UpdateBanner container={asContainer(sw)} enabled={false} />);
    await flush();
    expect(sw.register).not.toHaveBeenCalled();
  });

  it("tells the user when a new version has installed and waits, then Reload activates it and reloads", async () => {
    const sw = new FakeContainer(true);
    const reload = vi.fn();
    render(<UpdateBanner container={asContainer(sw)} enabled reload={reload} />);
    await flush();

    const worker = new FakeWorker();
    sw.registration.installing = worker;
    act(() => {
      sw.registration.dispatchEvent(new Event("updatefound"));
    });
    expect(screen.queryByTestId("update-banner")).not.toBeInTheDocument(); // still installing
    act(() => worker.setState("installed"));

    expect(screen.getByTestId("update-banner")).toHaveTextContent(t("pwa.updateAvailable"));
    await userEvent.click(screen.getByRole("button", { name: t("pwa.reload") }));
    expect(worker.postMessage).toHaveBeenCalledWith({ type: "SKIP_WAITING" });
    expect(reload).not.toHaveBeenCalled(); // only once the new worker controls the page
    act(() => {
      sw.dispatchEvent(new Event("controllerchange"));
    });
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it("shows the banner for a worker that was already waiting when the page loaded", async () => {
    const sw = new FakeContainer(true);
    const waiting = new FakeWorker();
    waiting.state = "installed";
    sw.registration.waiting = waiting;
    render(<UpdateBanner container={asContainer(sw)} enabled />);
    expect(await screen.findByTestId("update-banner")).toBeInTheDocument();
  });

  it("is silent on the very first install (no controller yet) and does not reload when the worker claims the page", async () => {
    const sw = new FakeContainer(false);
    const reload = vi.fn();
    render(<UpdateBanner container={asContainer(sw)} enabled reload={reload} />);
    await flush();
    const worker = new FakeWorker();
    sw.registration.installing = worker;
    act(() => {
      sw.registration.dispatchEvent(new Event("updatefound"));
    });
    act(() => worker.setState("installed"));
    act(() => {
      sw.dispatchEvent(new Event("controllerchange"));
    });
    expect(screen.queryByTestId("update-banner")).not.toBeInTheDocument();
    expect(reload).not.toHaveBeenCalled();
  });

  it("does not reload on a controller change the user did not ask for", async () => {
    const sw = new FakeContainer(true);
    const reload = vi.fn();
    render(<UpdateBanner container={asContainer(sw)} enabled reload={reload} />);
    await flush();
    act(() => {
      sw.dispatchEvent(new Event("controllerchange"));
    });
    expect(reload).not.toHaveBeenCalled();
  });

  it("survives a failed registration", async () => {
    const sw = new FakeContainer(true);
    sw.register.mockRejectedValueOnce(new Error("blocked"));
    render(<UpdateBanner container={asContainer(sw)} enabled />);
    await flush();
    expect(screen.queryByTestId("update-banner")).not.toBeInTheDocument();
  });
});
