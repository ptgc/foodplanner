import { useEffect, useState } from "react";

export type WakeLockStatus = "idle" | "active" | "unsupported" | "failed";

/**
 * Keeps the screen on while `enabled` (Screen Wake Lock API). Requests on start, requests again when the page becomes
 * visible (the browser releases the lock whenever the tab is hidden) and releases on disable and unmount. Where the
 * API is missing or refuses (low battery, permissions policy) the status says so, so the UI can show a hint; nothing throws.
 */
export function useWakeLock(enabled: boolean): WakeLockStatus {
  const supported = typeof navigator !== "undefined" && !!navigator.wakeLock;
  const [held, setHeld] = useState<"active" | "failed" | null>(null);

  useEffect(() => {
    if (!enabled || !supported) return;

    let stopped = false;
    let sentinel: WakeLockSentinel | null = null;

    const acquire = async () => {
      if (stopped || sentinel) return;
      try {
        const lock = await navigator.wakeLock.request("screen");
        if (stopped) {
          // Left the page while the request was in flight.
          void lock.release().catch(() => undefined);
          return;
        }
        sentinel = lock;
        lock.addEventListener("release", () => {
          if (sentinel === lock) sentinel = null;
        });
        setHeld("active");
      } catch {
        if (!stopped) setHeld("failed");
      }
    };

    const onVisibility = () => {
      if (document.visibilityState === "visible") void acquire();
    };

    void acquire();
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      stopped = true;
      document.removeEventListener("visibilitychange", onVisibility);
      const held = sentinel;
      sentinel = null;
      if (held) void held.release().catch(() => undefined);
    };
  }, [enabled, supported]);

  if (!enabled) return "idle";
  if (!supported) return "unsupported";
  return held ?? "idle";
}
