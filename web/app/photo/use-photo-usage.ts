import { useCallback, useEffect, useState } from "react";
import { api } from "../api/client";
import type { components } from "../api/schema";

export type PhotoUsage = components["schemas"]["PhotoUsageStatusResponse"];

export type UsageState = { status: "loading" } | { status: "error" } | { status: "ok"; usage: PhotoUsage };

/**
 * GET `imports/photo-usage`: whether the feature is on and how many photo imports are left today. Used by the recipes page
 * (only `enabled` matters there, so this file stays small and out of the upload screen's chunk) and by the upload screen.
 * `active` false skips the request (a Child never asks).
 */
export function usePhotoUsage(householdId: string, active: boolean) {
  const [state, setState] = useState<UsageState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    if (!active) return;
    let cancelled = false;
    void (async () => {
      try {
        const { data } = await api.GET("/api/households/{householdId}/imports/photo-usage", { params: { path: { householdId } } });
        if (!cancelled) setState(data ? { status: "ok", usage: data } : { status: "error" });
      } catch {
        if (!cancelled) setState({ status: "error" });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [householdId, active, attempt]);
  const retry = useCallback(() => {
    setState({ status: "loading" });
    setAttempt((n) => n + 1);
  }, []);
  /** Takes a newer count from a response (success `photoUsage`, or `remainingToday` of a problem). */
  const update = useCallback((remainingToday: number | null, unlimited?: boolean) => {
    setState((prev) =>
      prev.status === "ok" ? { status: "ok", usage: { ...prev.usage, remainingToday, unlimited: unlimited ?? prev.usage.unlimited } } : prev,
    );
  }, []);
  return { state, retry, update };
}
