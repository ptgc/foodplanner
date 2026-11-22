import { useSyncExternalStore } from "react";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { call } from "../household/call";

// The household's chosen Oda product per ingredient (T-052). Only Foodplanner's own endpoints are used here: picking and clearing never
// call Oda, and nothing about a product (id or name) is written to browser storage. The generated client is the only API client.

export type OdaPreference = components["schemas"]["OdaPreferenceResponse"];

/** Saves the household's choice for an ingredient: Oda's product id, taken from the fill's own alternatives. */
export function setPreference(householdId: string, ingredientId: string, productId: number) {
  return call(() =>
    api.PUT("/api/households/{householdId}/oda-preferences/{ingredientId}", {
      params: { path: { householdId, ingredientId } },
      body: { odaProductId: productId },
    }),
  );
}

export function clearPreference(householdId: string, ingredientId: string) {
  return call(() =>
    api.DELETE("/api/households/{householdId}/oda-preferences/{ingredientId}", { params: { path: { householdId, ingredientId } } }),
  );
}

export function listPreferences(householdId: string) {
  return call(() => api.GET("/api/households/{householdId}/oda-preferences", { params: { path: { householdId } } }));
}

function subscribe(listener: () => void): () => void {
  window.addEventListener("online", listener);
  window.addEventListener("offline", listener);
  return () => {
    window.removeEventListener("online", listener);
    window.removeEventListener("offline", listener);
  };
}

/** False when the browser knows it has no connection (the buttons that change choices are then disabled). */
export function useOnline(): boolean {
  return useSyncExternalStore(
    subscribe,
    () => navigator.onLine !== false,
    () => true,
  );
}
