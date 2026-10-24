import { useCallback, useEffect, useState } from "react";
import { api } from "../api/client";
import { useI18n, type I18n } from "../i18n";
import { problemKey, problemTypeOf } from "../i18n/problems";

type ApiState =
  | { kind: "loading" }
  | { kind: "ok"; status: string }
  // The reason is a ProblemDetails type code (translated on screen) or "unreachable" (network failure).
  | { kind: "error"; reason: "unreachable" | { problemType: string | undefined } };

// Unknown means the API itself could not be reached, so the database state cannot be told.
type DatabaseState = { kind: "loading" } | { kind: "up" } | { kind: "down" } | { kind: "unknown" };

type Health = { api: ApiState; database: DatabaseState };

const loading: Health = { api: { kind: "loading" }, database: { kind: "loading" } };

async function loadApi(): Promise<ApiState> {
  try {
    const { data, error } = await api.GET("/api/health");
    if (data) return { kind: "ok", status: data.status };
    return { kind: "error", reason: { problemType: problemTypeOf(error) } };
  } catch {
    return { kind: "error", reason: "unreachable" };
  }
}

async function loadDatabase(): Promise<DatabaseState> {
  try {
    const { data } = await api.GET("/api/health/ready");
    return data?.database === "Up" ? { kind: "up" } : { kind: "down" };
  } catch {
    return { kind: "unknown" };
  }
}

async function loadHealth(): Promise<Health> {
  const [apiState, database] = await Promise.all([loadApi(), loadDatabase()]);
  return { api: apiState, database };
}

const statusKeys = {
  healthy: "home.status.healthy",
  degraded: "home.status.degraded",
  unhealthy: "home.status.unhealthy",
} as const;

/** The server's status word, translated when known, otherwise shown as received. */
function statusText(t: I18n["t"], status: string): string {
  const key = statusKeys[status.toLowerCase() as keyof typeof statusKeys];
  return key ? t(key) : status;
}

function failureText(t: I18n["t"], reason: Extract<ApiState, { kind: "error" }>["reason"]): string {
  return reason === "unreachable" ? t("problem.unreachable") : t(problemKey(reason.problemType));
}

export default function Home() {
  const { t } = useI18n();
  const [state, setState] = useState<Health>(loading);

  useEffect(() => {
    let cancelled = false;
    void loadHealth().then((next) => {
      if (!cancelled) setState(next);
    });
    return () => {
      cancelled = true;
    };
  }, []);

  const retry = useCallback(() => {
    setState(loading);
    void loadHealth().then(setState);
  }, []);

  const busy = state.api.kind === "loading" || state.database.kind === "loading";

  return (
    <section aria-labelledby="health-heading">
      <h1 id="health-heading">{t("home.title")}</h1>
      <div role="status" aria-live="polite">
        {state.api.kind === "loading" && <p>{t("home.checkingApi")}</p>}
        {state.api.kind === "ok" && <p>{t("home.apiStatus", { status: statusText(t, state.api.status) })}</p>}
        {state.api.kind === "error" && (
          <p className="health-error">{t("home.apiFailed", { reason: failureText(t, state.api.reason) })}</p>
        )}
        {state.database.kind === "loading" && <p>{t("home.checkingDatabase")}</p>}
        {state.database.kind === "up" && <p>{t("home.databaseUp")}</p>}
        {state.database.kind === "down" && <p className="health-error">{t("home.databaseDown")}</p>}
        {state.database.kind === "unknown" && <p className="health-error">{t("home.databaseUnknown")}</p>}
      </div>
      <button type="button" className="button" onClick={retry} disabled={busy}>
        {t("home.checkAgain")}
      </button>
    </section>
  );
}
