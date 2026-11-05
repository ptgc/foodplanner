import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { call, failureKey } from "../household/call";
import type { MessageKey } from "../i18n";
import { problemTypeOf } from "../i18n/problems";
import { attemptsRemainingOf, retryAfterSeconds } from "./pin";

export type Profile = components["schemas"]["ProfileResponse"];

const problem = (code: string) => `urn:foodplanner:problem:${code}`;

/** The household's kid profiles (the picker and the settings). `null` while loading or when the list could not be read. */
export function useProfiles(householdId: string) {
  const [state, setState] = useState<{ householdId: string; profiles: Profile[] | null; failed: boolean } | null>(null);
  const [tick, setTick] = useState(0);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      let profiles: Profile[] | null = null;
      try {
        const { data } = await api.GET("/api/households/{householdId}/profiles", { params: { path: { householdId } } });
        profiles = data ?? null;
      } catch {
        profiles = null;
      }
      if (!cancelled) setState({ householdId, profiles, failed: profiles === null });
    })();
    return () => {
      cancelled = true;
    };
  }, [householdId, tick]);

  const current = state?.householdId === householdId ? state : null;
  return {
    profiles: current?.profiles ?? null,
    loading: current === null,
    failed: current?.failed ?? false,
    reload: () => setTick((n) => n + 1),
  };
}

export type ActivateResult = { kind: "ok" } | { kind: "pin-not-set" } | { kind: "failed"; key: MessageKey };

/** Makes the device act as the kid profile. An adult without a PIN is refused (`pin-not-set`): the picker then asks for one. */
export async function activateProfile(householdId: string, profileId: string): Promise<ActivateResult> {
  try {
    const { response, error } = await api.POST("/api/households/{householdId}/profiles/{profileId}/activate", {
      params: { path: { householdId, profileId } },
    });
    if (response.ok) return { kind: "ok" };
    const type = problemTypeOf(error);
    if (type === problem("pin-not-set")) return { kind: "pin-not-set" };
    return { kind: "failed", key: failureKey(response.status, error) };
  } catch {
    return { kind: "failed", key: "problem.unreachable" };
  }
}

export type UnlockResult =
  | { kind: "ok" }
  /** A wrong PIN; `attemptsRemaining` is null if the server did not say. */
  | { kind: "wrong"; attemptsRemaining: number | null }
  /** Too many wrong PINs (or too many tries): locked for `seconds` (null: unknown). */
  | { kind: "locked"; seconds: number | null }
  | { kind: "failed"; key: MessageKey };

/** Returns the device from kid mode to the full session. The server counts wrong tries per account and locks. */
export async function unlockWithPin(pin: string): Promise<UnlockResult> {
  try {
    const { response, error } = await api.POST("/api/auth/pin/unlock", { body: { pin } });
    if (response.ok) return { kind: "ok" };
    const type = problemTypeOf(error);
    if (response.status === 429 || type === problem("pin-locked")) {
      return { kind: "locked", seconds: retryAfterSeconds(response.headers.get("Retry-After")) };
    }
    if (type === problem("pin-incorrect")) return { kind: "wrong", attemptsRemaining: attemptsRemainingOf(error) };
    if (type === problem("not-in-kid-mode")) return { kind: "ok" }; // already unlocked (another tab): nothing left to do
    if (type === problem("pin-not-set")) return { kind: "failed", key: "kid.problem.pinNotSet" };
    return { kind: "failed", key: response.status === 403 ? "problem.forbidden" : "problem.generic" };
  } catch {
    return { kind: "failed", key: "problem.unreachable" };
  }
}

export type SetPinResult =
  | { kind: "ok" }
  | { kind: "wrong-current"; attemptsRemaining: number | null }
  | { kind: "locked" }
  | { kind: "invalid" }
  | { kind: "needs-current" }
  | { kind: "failed"; key: MessageKey };

/** Sets the signed-in account's PIN, or replaces it (then `currentPin` is required). */
export async function setPin(pin: string, currentPin?: string): Promise<SetPinResult> {
  try {
    const { response, error } = await api.PUT("/api/auth/pin", {
      body: { pin, currentPin: currentPin ?? null, password: null },
    });
    if (response.ok) return { kind: "ok" };
    const type = problemTypeOf(error);
    if (type === problem("pin-incorrect")) return { kind: "wrong-current", attemptsRemaining: attemptsRemainingOf(error) };
    if (type === problem("pin-locked") || response.status === 429) return { kind: "locked" };
    if (response.status === 400) {
      const errors = (error && "errors" in error ? error.errors : undefined) ?? {};
      if ("currentPin" in errors) return { kind: "needs-current" };
      if ("pin" in errors) return { kind: "invalid" };
      return { kind: "failed", key: "problem.generic" };
    }
    return { kind: "failed", key: response.status === 403 ? "problem.forbidden" : "problem.generic" };
  } catch {
    return { kind: "failed", key: "problem.unreachable" };
  }
}

export type LikeResult = { ok: true } | { ok: false; key: MessageKey };

/** Likes (or unlikes) a recipe as the active profile. The server takes the profile from the session. */
export async function setLike(householdId: string, recipeId: string, liked: boolean): Promise<LikeResult> {
  const params = { params: { path: { householdId, recipeId } } };
  const result = await call(() =>
    liked
      ? api.PUT("/api/households/{householdId}/recipes/{recipeId}/like", params)
      : api.DELETE("/api/households/{householdId}/recipes/{recipeId}/like", params),
  );
  return result.ok ? { ok: true } : { ok: false, key: result.key };
}
