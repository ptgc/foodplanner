import { useEffect, useId, useState } from "react";
import { Link } from "react-router";
import { ConfirmDialog } from "../household/confirm-dialog";
import { useI18n, type MessageKey } from "../i18n";
import { navigation, rememberSyncPending, type OdaSyncPending } from "./oda";
import { saveSyncChoices, startSync, type OdaSyncOutcome, type OdaSyncReview, type OdaSyncRow } from "./oda-sync";

type Category = OdaSyncRow["category"];
const categories: Category[] = ["usually-bought", "suggestion", "current", "not-found"];

const sectionKeys: Record<Category, MessageKey> = {
  "usually-bought": "oda.sync.section.usually-bought",
  suggestion: "oda.sync.section.suggestion",
  current: "oda.sync.section.current",
  "not-found": "oda.sync.section.not-found",
};

const failureKeys: Record<Extract<OdaSyncOutcome, { kind: "failed" }>["reason"], MessageKey> = {
  "state-invalid": "oda.problem.stateInvalid",
  "rate-limited": "problem.rateLimited",
  forbidden: "problem.forbidden",
  "not-configured": "oda.sync.notConfigured",
  "nothing-to-sync": "oda.sync.problem.nothing",
  unavailable: "oda.problem.unavailable",
  failed: "problem.generic",
};

const stoppedKeys: Record<"time" | "oda-limit" | "oda-error", MessageKey> = {
  time: "oda.sync.partial.time",
  "oda-limit": "oda.sync.partial.oda-limit",
  "oda-error": "oda.sync.partial.oda-error",
};

/**
 * The review of "Sync with Oda" (T-130), rendered in place by the Oda callback page. The result of the one `review` call (names and ids
 * from Oda) lives in this component's state only: it is never written to browser storage, never cached, and lost on reload (the screen
 * says so). Product names are plain text (React escapes them). Saving sends only the ticked rows' ids; no Oda connection exists any more.
 */
export default function OdaSyncReviewScreen({ pending, outcome }: { pending: OdaSyncPending; outcome: Promise<OdaSyncOutcome> }) {
  const { t } = useI18n();
  return (
    <>
      <h1 id="oda-title">{t("oda.sync.title")}</h1>
      <Outcome pending={pending} outcome={outcome} />
    </>
  );
}

function Outcome({ pending, outcome }: { pending: OdaSyncPending; outcome: Promise<OdaSyncOutcome> }) {
  const { t } = useI18n();
  const [result, setResult] = useState<OdaSyncOutcome | null>(null);

  useEffect(() => {
    let live = true;
    void outcome.then((o) => {
      if (live) setResult(o);
    });
    return () => {
      live = false;
    };
  }, [outcome]);

  if (result === null) {
    return (
      <p role="status" data-testid="oda-sync-working">
        {t("oda.sync.working")}
      </p>
    );
  }

  const back = (
    <Link className="button button-outline oda-back" to="/shopping/oda-choices">
      {t("oda.sync.back")}
    </Link>
  );
  if (result.kind === "failed") {
    return (
      <div data-testid="oda-sync-failed">
        <p role="alert" className="form-error">
          {t(failureKeys[result.reason])}
        </p>
        {result.connectionClosed === false && <p role="alert">{t("oda.result.notClosed")}</p>}
        <Link className="button oda-back" to="/shopping/oda-choices">
          {t("oda.sync.tryAgain")}
        </Link>
      </div>
    );
  }
  if (result.review.status === "cancelled") {
    return (
      <div>
        <p data-testid="oda-sync-cancelled">{t("oda.sync.cancelled")}</p>
        {back}
      </div>
    );
  }
  return <Review review={result.review} pending={pending} />;
}

function Review({ review, pending }: { review: OdaSyncReview; pending: OdaSyncPending }) {
  const { t } = useI18n();
  const { householdId } = pending;
  const [rows, setRows] = useState<OdaSyncRow[]>(review.rows);
  const [chosen, setChosen] = useState<Record<string, number>>(() =>
    Object.fromEntries(review.rows.filter((r) => r.selectedProductId !== null).map((r) => [r.ingredientId, Number(r.selectedProductId)])),
  );
  const [ticked, setTicked] = useState<Record<string, boolean>>(() =>
    Object.fromEntries(review.rows.filter((r) => r.preticked && r.selectedProductId !== null).map((r) => [r.ingredientId, true])),
  );
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const [saved, setSaved] = useState<{ count: number; kept: number } | null>(null);
  const [createdTotal, setCreatedTotal] = useState(0);
  const [next, setNext] = useState(false);
  const [nextBusy, setNextBusy] = useState(false);
  const [nextError, setNextError] = useState<MessageKey | null>(null);

  const productName = (row: OdaSyncRow, id: number | undefined) =>
    id === undefined ? null : (row.options.find((o) => Number(o.productId) === id)?.productName ?? null);
  const tickedRows = rows.filter((r) => ticked[r.ingredientId] && chosen[r.ingredientId] !== undefined);
  const count = (c: Category) => review.rows.filter((r) => r.category === c).length;
  const stopped = review.stopped;
  const notChecked = Number(review.notCheckedCount);

  const setOne = (row: OdaSyncRow, on: boolean) => setTicked((s) => ({ ...s, [row.ingredientId]: on }));
  const setSection = (category: Category, on: boolean) =>
    setTicked((s) => ({ ...s, ...Object.fromEntries(rows.filter((r) => r.category === category && chosen[r.ingredientId] !== undefined).map((r) => [r.ingredientId, on])) }));
  const choose = (row: OdaSyncRow, productId: number, isCurrent: boolean) => {
    if (row.category === "current" && isCurrent) {
      setChosen((s) => {
        const rest = { ...s };
        delete rest[row.ingredientId];
        return rest;
      });
      setOne(row, false);
      return;
    }
    setChosen((s) => ({ ...s, [row.ingredientId]: productId }));
    setOne(row, true);
  };

  const save = async () => {
    setBusy(true);
    setError(null);
    const items = tickedRows.map((r) => ({ ingredientId: r.ingredientId, odaProductId: chosen[r.ingredientId]!, replace: r.hasExistingChoice }));
    const result = await saveSyncChoices(householdId, items);
    setBusy(false);
    if (!result.ok) {
      setError(result.key); // the list and the ticks stay
      return;
    }
    const data = result.data;
    const skipped = new Set(data?.skippedExisting ?? []);
    const done = new Set(items.map((i) => i.ingredientId).filter((id) => !skipped.has(id)));
    setRows((rs) => rs.filter((r) => !done.has(r.ingredientId)));
    setTicked({});
    setCreatedTotal((n) => n + Number(data?.created ?? 0));
    setSaved({ count: done.size, kept: skipped.size });
  };

  // The next run continues behind what this one searched. Choices saved for new ingredients are no longer in the list of the next run
  // (unless existing choices are included), so the offset moves back by that many.
  const nextOffset = Math.max(0, pending.offset + review.rows.length - (pending.includeExisting ? 0 : createdTotal));
  const startNext = async () => {
    setNextBusy(true);
    setNextError(null);
    const options = { includeStaples: pending.includeStaples, includeExisting: pending.includeExisting, offset: nextOffset };
    const result = await startSync(householdId, options);
    if (result.kind === "ready") {
      rememberSyncPending({ householdId, ...options });
      navigation.go(result.url);
      return;
    }
    setNextBusy(false);
    setNextError(result.reason === "nothing-to-sync" ? "oda.sync.problem.nothing" : result.reason === "rate-limited" ? "problem.rateLimited" : "problem.generic");
  };

  return (
    <div className="oda-sync" data-testid="oda-sync">
      <p data-testid="oda-sync-summary">
        {t("oda.sync.summary", { usual: count("usually-bought"), suggestions: count("suggestion"), notFound: count("not-found") })}
      </p>
      <p className="shop-hint">{t("oda.sync.notKept")}</p>
      {!review.connectionClosed && (
        <p role="alert" className="form-error" data-testid="oda-sync-not-closed">
          {t("oda.result.notClosed")}
        </p>
      )}
      {review.usuallyBought === "unavailable" && <p data-testid="oda-sync-usual-unavailable">{t("oda.sync.usualUnavailable")}</p>}
      {(stopped !== null || notChecked > 0) && (
        <div data-testid="oda-sync-partial">
          {stopped !== null && <p role="status">{t(stoppedKeys[stopped])}</p>}
          {notChecked > 0 && <p>{t("oda.sync.notChecked", { count: notChecked })}</p>}
          {notChecked > 0 && (
            <button type="button" className="button button-outline oda-pick-button" onClick={() => setNext(true)}>
              {t("oda.sync.next")}
            </button>
          )}
        </div>
      )}
      {saved && (
        <div role="status" data-testid="oda-sync-saved">
          <p>{t("oda.sync.saved", { count: saved.count })}</p>
          {saved.kept > 0 && <p>{t("oda.sync.kept", { count: saved.kept })}</p>}
          <Link className="button button-outline oda-back" to="/shopping/oda-choices">
            {t("oda.choices.link")}
          </Link>
        </div>
      )}
      {categories.map((category) => {
        const inSection = rows.filter((r) => r.category === category);
        if (inSection.length === 0) return null;
        const bulk = category === "usually-bought" || category === "suggestion";
        const allOn = inSection.every((r) => ticked[r.ingredientId]);
        const section = t(sectionKeys[category]);
        return (
          <section key={category} className="oda-sync-section" aria-label={section} data-testid={`oda-sync-${category}`}>
            <h2>{section}</h2>
            {bulk && (
              <button
                type="button"
                className="button button-outline oda-pick-button"
                aria-label={t(allOn ? "oda.sync.untickAllFor" : "oda.sync.tickAllFor", { section })}
                onClick={() => setSection(category, !allOn)}
              >
                {t(allOn ? "oda.sync.untickAll" : "oda.sync.tickAll")}
              </button>
            )}
            <ul className="oda-sync-list">
              {inSection.map((row) =>
                category === "not-found" ? (
                  <li key={row.ingredientId} className="oda-sync-notfound" data-testid="oda-sync-row">
                    {row.ingredientName}
                    {row.reason ? `: ${t(row.reason === "no-match" ? "oda.sync.reason.no-match" : "oda.sync.reason.unavailable")}` : ""}
                  </li>
                ) : (
                  <SyncRow
                    key={row.ingredientId}
                    row={row}
                    chosenId={chosen[row.ingredientId]}
                    on={ticked[row.ingredientId] === true}
                    productName={productName(row, chosen[row.ingredientId])}
                    onTick={(on) => setOne(row, on)}
                    onChoose={(o) => choose(row, Number(o.productId), o.isCurrent)}
                  />
                ),
              )}
            </ul>
          </section>
        );
      })}
      {error && (
        <p role="alert" className="form-error" data-testid="oda-sync-error">
          {t(error)}
        </p>
      )}
      <div className="oda-sync-savebar">
        <button type="button" className="button" disabled={busy || tickedRows.length === 0} onClick={() => void save()}>
          {busy ? t("oda.sync.saving") : t("oda.sync.save", { count: tickedRows.length })}
        </button>
      </div>
      {next && (
        <ConfirmDialog
          title={t("oda.sync.next")}
          body={t("oda.sync.next.body")}
          confirmLabel={t("oda.sync.confirm.submit")}
          tone="primary"
          busy={nextBusy}
          error={nextError}
          onConfirm={() => void startNext()}
          onCancel={() => setNext(false)}
        />
      )}
    </div>
  );
}

function SyncRow({
  row,
  chosenId,
  on,
  productName,
  onTick,
  onChoose,
}: {
  row: OdaSyncRow;
  chosenId: number | undefined;
  on: boolean;
  productName: string | null;
  onTick: (on: boolean) => void;
  onChoose: (option: OdaSyncRow["options"][number]) => void;
}) {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  const listId = useId();
  const group = useId();
  const current = row.options.find((o) => o.isCurrent);
  const selected = chosenId ?? (row.category === "current" && current ? Number(current.productId) : undefined);
  return (
    <li className="oda-sync-row" data-testid="oda-sync-row">
      <label className="oda-sync-label">
        <input type="checkbox" checked={on} disabled={chosenId === undefined} onChange={(e) => onTick(e.target.checked)} />
        <span className="oda-sync-text">
          <span className="oda-sync-ingredient">{row.ingredientName}</span>
          <span className="oda-sync-product">{productName ?? t("oda.sync.currentStays")}</span>
          {row.hasExistingChoice && row.category !== "current" && <span className="oda-sync-note">{t("oda.sync.replaces")}</span>}
        </span>
      </label>
      {row.options.length > 1 && (
        <>
          <button
            type="button"
            className="button button-outline oda-pick-button"
            aria-expanded={open}
            aria-controls={listId}
            aria-label={t("oda.sync.chooseAnotherFor", { name: row.ingredientName })}
            onClick={() => setOpen((o) => !o)}
          >
            {t("oda.sync.chooseAnother")}
          </button>
          {open && (
            <ul id={listId} className="oda-alternatives" aria-label={t("oda.sync.optionsLabel", { name: row.ingredientName })}>
              {row.options.map((o) => (
                <li key={String(o.productId)} className="oda-sync-option" data-testid="oda-sync-option">
                  <label className="oda-sync-option-label">
                    <input type="radio" name={group} checked={selected === Number(o.productId)} onChange={() => onChoose(o)} />
                    <span className="oda-alternative-name">{o.productName}</span>
                    {o.isUsual && <span className="oda-alternative-mark">{t("oda.sync.usualMark")}</span>}
                    {o.isCurrent && <span className="oda-alternative-mark">{t("oda.sync.currentMark")}</span>}
                  </label>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </li>
  );
}

