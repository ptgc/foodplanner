import { useCallback, useEffect, useState } from "react";
import { api } from "../api/client";

type HealthState =
  | { kind: "loading" }
  | { kind: "ok"; status: string }
  | { kind: "error"; message: string };

async function loadHealth(): Promise<HealthState> {
  try {
    const { data, error, response } = await api.GET("/api/health");
    if (data) return { kind: "ok", status: data.status };
    const detail = error?.title ?? `HTTP ${response.status}`;
    return { kind: "error", message: detail };
  } catch {
    return { kind: "error", message: "Could not reach the API." };
  }
}

export default function Home() {
  const [state, setState] = useState<HealthState>({ kind: "loading" });

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
    setState({ kind: "loading" });
    void loadHealth().then(setState);
  }, []);

  return (
    <section aria-labelledby="health-heading">
      <h1 id="health-heading">API status</h1>
      <div role="status" aria-live="polite">
        {state.kind === "loading" && <p>Checking the API...</p>}
        {state.kind === "ok" && <p>API is {state.status}.</p>}
        {state.kind === "error" && <p className="health-error">API check failed: {state.message}</p>}
      </div>
      <button type="button" className="button" onClick={retry} disabled={state.kind === "loading"}>
        Check again
      </button>
    </section>
  );
}
