import { useCallback, useEffect, useRef, useState } from "react";

/** The part of `navigator.serviceWorker` used here (so tests can fake it). */
export type SwContainer = Pick<ServiceWorkerContainer, "register" | "addEventListener" | "removeEventListener" | "controller">;

export type ServiceWorkerOptions = {
  /** Default: `navigator.serviceWorker`. */
  container?: SwContainer;
  /** Default: only in the production build (the dev server has no sw.js). */
  enabled?: boolean;
  /** Default: `location.reload()`. */
  reload?: () => void;
  /** How often to look for a new version while the page stays open, in ms. */
  checkEveryMs?: number;
};

const hour = 60 * 60 * 1000;

/**
 * Registers /sw.js and tells when a new version is installed and waiting. `applyUpdate` asks the waiting worker to
 * take over (it answers to SKIP_WAITING) and reloads once it controls the page. A reload is never done on the very
 * first install, where the worker only claims the page.
 */
export function useServiceWorkerUpdate(options: ServiceWorkerOptions = {}) {
  const { enabled = import.meta.env.PROD, reload = () => window.location.reload(), checkEveryMs = hour } = options;
  const container = options.container ?? (typeof navigator !== "undefined" ? navigator.serviceWorker : undefined);
  const [waiting, setWaiting] = useState<ServiceWorker | null>(null);
  const applying = useRef(false);
  const reloadRef = useRef(reload);
  useEffect(() => {
    reloadRef.current = reload;
  });

  useEffect(() => {
    if (!enabled || !container) return;
    let cancelled = false;
    let registration: ServiceWorkerRegistration | undefined;
    let timer: ReturnType<typeof setInterval> | undefined;

    const watch = (worker: ServiceWorker) => {
      const onState = () => {
        // "installed" with a controller means an older version is running: this one is an update, not the first install.
        if (worker.state === "installed" && container.controller && !cancelled) setWaiting(worker);
      };
      worker.addEventListener("statechange", onState);
      onState();
    };
    const onUpdateFound = () => {
      if (registration?.installing) watch(registration.installing);
    };
    const onControllerChange = () => {
      if (applying.current) reloadRef.current();
    };
    const check = () => void registration?.update().catch(() => undefined);
    const onVisible = () => {
      if (document.visibilityState === "visible") check();
    };

    container.addEventListener("controllerchange", onControllerChange);
    document.addEventListener("visibilitychange", onVisible);
    container
      .register("/sw.js", { scope: "/" })
      .then((reg) => {
        if (cancelled) return;
        registration = reg;
        reg.addEventListener("updatefound", onUpdateFound);
        if (reg.waiting) watch(reg.waiting);
        if (reg.installing) watch(reg.installing);
        timer = setInterval(check, checkEveryMs);
      })
      .catch(() => {
        // No service worker (blocked, unsupported, private mode): the app works as before, just not offline.
      });

    return () => {
      cancelled = true;
      container.removeEventListener("controllerchange", onControllerChange);
      document.removeEventListener("visibilitychange", onVisible);
      registration?.removeEventListener("updatefound", onUpdateFound);
      if (timer) clearInterval(timer);
    };
  }, [container, enabled, checkEveryMs]);

  const applyUpdate = useCallback(() => {
    if (!waiting) return;
    applying.current = true;
    waiting.postMessage({ type: "SKIP_WAITING" });
  }, [waiting]);

  return { updateAvailable: waiting !== null, applyUpdate };
}
