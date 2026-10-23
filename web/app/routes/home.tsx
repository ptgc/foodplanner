import { useCallback, useEffect, useState } from "react";
import { api } from "../api/client";

type ApiState =
  | { kind: "loading" }
  | { kind: "ok"; status: string }
  | { kind: "error"; message: string };

// Unknown means the API itself could not be reached, so the database state cannot be told.
type DatabaseState = { kind: "loading" } | { kind: "up" } | { kind: "down" } | { kind: "unknown" };

type Health = { api: ApiState; database: DatabaseState };

const loading: Health = { api: { kind: "loading" }, database: { kind: "loading" } };

async function loadApi(): Promise<ApiState> {
  try {
    const { data, error, response } = await api.GET("/api/health");
    if (data) return { kind: "ok", status: data.status };
    const detail = error?.title ?? `HTTP ${response.status}`;
    return { kind: "error", message: detail };
  } catch {
    return { kind: "error", message: "Could not reach the API." };
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

export default function Home() {
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
      <h1 id="health-heading">API status</h1>
      <div role="status" aria-live="polite">
        {state.api.kind === "loading" && <p>Checking the API...</p>}
        {state.api.kind === "ok" && <p>API is {state.api.status}.</p>}
        {state.api.kind === "error" && <p className="health-error">API check failed: {state.api.message}</p>}
        {state.database.kind === "loading" && <p>Checking the database...</p>}
        {state.database.kind === "up" && <p>Database: Up</p>}
        {state.database.kind === "down" && <p className="health-error">Database: Down</p>}
        {state.database.kind === "unknown" && <p className="health-error">Database: Unknown</p>}
      </div>
      <button type="button" className="button" onClick={retry} disabled={busy}>
        Check again
      </button>
    </section>
  );
}
