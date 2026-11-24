import { Suspense, useEffect, useState } from "react";
import { Link } from "react-router";
import { useI18n } from "../i18n";
import { lazyWithMessages } from "../i18n/lazy";
import { completeOda, takeAnyPending, type OdaCompleteResult, type OdaPending, type OdaSyncPending } from "../shopping/oda";
import { reviewSync, type OdaSyncOutcome } from "../shopping/oda-sync";
import { OdaResult } from "../shopping/oda-result";

// The review of "Sync with Oda" (T-130): code and texts arrive together, only after the callback saw a sync pending record.
const SyncReview = lazyWithMessages("oda", () => import("../shopping/oda-sync-review"));

type Outcome =
  | { kind: "nothing" }
  | { kind: "done"; result: OdaCompleteResult; pending: OdaPending }
  | { kind: "sync"; outcome: Promise<OdaSyncOutcome>; pending: OdaSyncPending };

// One run per page load. Cart quantities are deltas, so the call to `oda/cart` must never happen twice: not from a
// second effect run (StrictMode, remounts), not from a reload (the code is gone from the address bar by then) and not
// from back navigation. The sync review is one-run too (its flow cookie is single use). This is in memory only; nothing
// about Oda is written to browser storage.
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
  const pending = takeAnyPending();
  if (!state || (!code && !error) || !pending) return Promise.resolve({ kind: "nothing" });
  if (pending.kind === "sync") {
    // The page shows the (lazy) review at once; it waits for this one request.
    return Promise.resolve({ kind: "sync", pending, outcome: reviewSync(pending.householdId, { state, code: error ? null : code, error }) });
  }
  return completeOda(pending.householdId, pending.weekStart, { state, code: error ? null : code, error }).then(
    (result): Outcome => ({ kind: "done", result, pending: { householdId: pending.householdId, weekStart: pending.weekStart } }),
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
      {outcome?.kind !== "sync" && <h1 id="oda-title">{t("oda.callback.title")}</h1>}
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
      {outcome?.kind === "sync" && (
        <Suspense fallback={null}>
          <SyncReview pending={outcome.pending} outcome={outcome.outcome} />
        </Suspense>
      )}
    </section>
  );
}
