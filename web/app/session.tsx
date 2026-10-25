import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { api, resetAntiforgeryToken } from "./api/client";
import type { components } from "./api/schema";
import { problemTypeOf } from "./i18n/problems";
import { clearRuntimeCaches } from "./pwa/clear-caches";
import { clearShoppingStorage } from "./shopping/storage";

const loginFailed = "urn:foodplanner:problem:login-failed";
const rateLimited = "urn:foodplanner:problem:rate-limited";

type Me = components["schemas"]["MeResponse"];
type Household = components["schemas"]["HouseholdResponse"];

export type Session =
  | { status: "loading" }
  | { status: "signedOut" }
  | { status: "signedIn"; user: Me; households: Household[]; activeHouseholdId: string | null };

export type SignInResult =
  | { kind: "ok" }
  | { kind: "cookie-missing" }
  // The server refused or something failed; `problemType` is the ProblemDetails `type` code, if known.
  | { kind: "problem"; problemType: string | undefined };

type SessionApi = {
  session: Session;
  signIn: (email: string, password: string) => Promise<SignInResult>;
  signOut: () => Promise<void>;
  setActiveHousehold: (id: string) => void;
};

const storageKey = (userId: string) => `foodplanner.activeHousehold.${userId}`;

function readStored(userId: string): string | null {
  try {
    return window.localStorage.getItem(storageKey(userId));
  } catch {
    return null;
  }
}

function writeStored(userId: string, id: string) {
  try {
    window.localStorage.setItem(storageKey(userId), id);
  } catch {
    // Storage may be blocked (private mode); the choice then lasts for this page only.
  }
}

/** Active household: the stored choice if still a member, else the first one. */
function pickActive(userId: string, households: Household[]): string | null {
  const stored = readStored(userId);
  if (stored && households.some((h) => h.id === stored)) return stored;
  return households[0]?.id ?? null;
}

const sessionKey = "foodplanner.session";

type SavedSession = { user: Me; households: Household[] };

function saveSession(value: SavedSession) {
  try {
    window.localStorage.setItem(sessionKey, JSON.stringify(value));
  } catch {
    // Without storage the app just cannot start while the API is unreachable.
  }
}

function clearSavedSession() {
  try {
    window.localStorage.removeItem(sessionKey);
  } catch {
    // Nothing to clear.
  }
}

/** The last session the server confirmed, used only when the API cannot be reached (offline shopping list). */
function readSavedSession(): Session | null {
  try {
    const raw = window.localStorage.getItem(sessionKey);
    if (!raw) return null;
    const saved = JSON.parse(raw) as Partial<SavedSession> | null;
    if (!saved?.user?.userId || !Array.isArray(saved.households)) return null;
    return {
      status: "signedIn",
      user: saved.user,
      households: saved.households,
      activeHouseholdId: pickActive(saved.user.userId, saved.households),
    };
  } catch {
    return null;
  }
}

async function loadSession(): Promise<Session> {
  try {
    const me = await api.GET("/api/auth/me");
    if (!me.data) {
      // The server answered: not signed in. Do not keep a stale offline identity.
      clearSavedSession();
      void clearRuntimeCaches();
      return { status: "signedOut" };
    }
    const list = await api.GET("/api/households");
    const households = list.data ?? [];
    saveSession({ user: me.data, households });
    return {
      status: "signedIn",
      user: me.data,
      households,
      activeHouseholdId: pickActive(me.data.userId, households),
    };
  } catch {
    // Network failure (not a 401): fall back to the saved session so cached pages can render offline.
    return readSavedSession() ?? { status: "signedOut" };
  }
}

const SessionContext = createContext<SessionApi | null>(null);

export function SessionProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session>({ status: "loading" });

  useEffect(() => {
    let cancelled = false;
    void loadSession().then((next) => {
      if (!cancelled) setSession(next);
    });
    return () => {
      cancelled = true;
    };
  }, []);

  const signIn = useCallback(async (email: string, password: string): Promise<SignInResult> => {
    try {
      const { response, error } = await api.POST("/api/auth/login", { body: { email, password } });
      // The token used for login was issued to an anonymous caller; get a fresh one for the new identity.
      resetAntiforgeryToken();
      if (!response.ok) {
        // Prefer the ProblemDetails type; a proxy or older server may only give the status.
        const byStatus = response.status === 429 ? rateLimited : response.status === 401 ? loginFailed : undefined;
        return { kind: "problem", problemType: problemTypeOf(error) ?? byStatus };
      }
      // Whatever the previous user left in the service worker's caches is not for this user.
      await clearRuntimeCaches();
      const next = await loadSession();
      if (next.status !== "signedIn") return { kind: "cookie-missing" };
      setSession(next);
      return { kind: "ok" };
    } catch {
      return { kind: "problem", problemType: undefined };
    }
  }, []);

  const signOut = useCallback(async () => {
    try {
      await api.POST("/api/auth/logout");
    } finally {
      clearSavedSession();
      // Recipes the service worker kept for this user must not outlive the session (shared devices).
      await clearRuntimeCaches();
      clearShoppingStorage();
      resetAntiforgeryToken();
      setSession({ status: "signedOut" });
    }
  }, []);

  const setActiveHousehold = useCallback((id: string) => {
    setSession((current) => {
      if (current.status !== "signedIn" || !current.households.some((h) => h.id === id)) return current;
      writeStored(current.user.userId, id);
      return { ...current, activeHouseholdId: id };
    });
  }, []);

  const value = useMemo(
    () => ({ session, signIn, signOut, setActiveHousehold }),
    [session, signIn, signOut, setActiveHousehold],
  );
  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): SessionApi {
  const ctx = useContext(SessionContext);
  if (!ctx) throw new Error("useSession must be used inside SessionProvider");
  return ctx;
}
