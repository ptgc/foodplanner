import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { api, resetAntiforgeryToken } from "./api/client";
import type { components } from "./api/schema";

type Me = components["schemas"]["MeResponse"];
type Household = components["schemas"]["HouseholdResponse"];

export type Session =
  | { status: "loading" }
  | { status: "signedOut" }
  | { status: "signedIn"; user: Me; households: Household[]; activeHouseholdId: string | null };

export type SignInResult =
  | { kind: "ok" }
  | { kind: "invalid" }
  | { kind: "rate-limited" }
  | { kind: "cookie-missing" }
  | { kind: "error" };

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

async function loadSession(): Promise<Session> {
  try {
    const me = await api.GET("/api/auth/me");
    if (!me.data) return { status: "signedOut" };
    const list = await api.GET("/api/households");
    const households = list.data ?? [];
    return {
      status: "signedIn",
      user: me.data,
      households,
      activeHouseholdId: pickActive(me.data.userId, households),
    };
  } catch {
    return { status: "signedOut" };
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
      const { response } = await api.POST("/api/auth/login", { body: { email, password } });
      // The token used for login was issued to an anonymous caller; get a fresh one for the new identity.
      resetAntiforgeryToken();
      if (response.status === 429) return { kind: "rate-limited" };
      if (response.status === 401) return { kind: "invalid" };
      if (!response.ok) return { kind: "error" };
      const next = await loadSession();
      if (next.status !== "signedIn") return { kind: "cookie-missing" };
      setSession(next);
      return { kind: "ok" };
    } catch {
      return { kind: "error" };
    }
  }, []);

  const signOut = useCallback(async () => {
    try {
      await api.POST("/api/auth/logout");
    } finally {
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
