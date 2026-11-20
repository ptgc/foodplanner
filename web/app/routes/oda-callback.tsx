import { useEffect, useState } from "react";
import { Link } from "react-router";
import { useI18n } from "../i18n";
import { completeOda, takePending, type OdaCompleteResult, type OdaPending } from "../shopping/oda";
import { OdaResult } from "../shopping/oda-result";

type Outcome = { kind: "nothing" } | { kind: "done"; result: OdaCompleteResult; pending: OdaPending };

// One run per page load. Cart quantities are deltas, so the call to `oda/cart` must never happen twice: not from a
// second effect run (StrictMode, remounts), not from a reload (the code is gone from the address bar by then) and not
// from back navigation. This is in memory only; nothing about Oda is written to browser storage.
let run: Promise<Outcome> | null = null;

/** For tests: forget the run of the previous page load. */
export function resetOdaCallbackForTests(): void {
  run = null;
}

function begin(): Promise<Outcome> {
  const query = new URLSearchParams(window.location.search);
  const state = query.get("state");
  const code = query.get("code");
  const error = query.get("error");
  // The code is one-time and secret: off the address bar (and out of history) before anything else happens.
  window.history.replaceState(window.history.state, "", window.location.pathname);
  const pending = takePending();
  if (!state || (!code && !error) || !pending) return Promise.resolve({ kind: "nothing" });
  return completeOda(pending.householdId, pending.weekStart, { state, code: error ? null : code, error }).then(
    (result): Outcome => ({ kind: "done", result, pending }),
  );
}

/** Where Oda sends the browser back to (`/oda/callback`, the redirect URI the API builds). */
export default function OdaCallback() {
  const { t } = useI18n();
  const [outcome, setOutcome] = useState<Outcome | null>(null);

  useEffect(() => {
    let live = true;
    run ??= begin();
    void run.then((o) => {
      if (live) setOutcome(o);
    });
    return () => {
      live = false;
    };
  }, []);

  return (
    <section className="oda-callback" aria-labelledby="oda-title">
      <h1 id="oda-title">{t("oda.callback.title")}</h1>
      {outcome === null && <p role="status">{t("oda.callback.working")}</p>}
      {outcome?.kind === "nothing" && (
        <>
          <p data-testid="oda-nothing">{t("oda.callback.nothingPending")}</p>
          <Link className="button button-outline oda-back" to="/shopping">
            {t("oda.result.back")}
          </Link>
        </>
      )}
      {outcome?.kind === "done" && <OdaResult result={outcome.result} pending={outcome.pending} />}
    </section>
  );
}
