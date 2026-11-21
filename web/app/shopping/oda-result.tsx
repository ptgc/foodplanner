import { Link } from "react-router";
import { useI18n, type MessageKey } from "../i18n";
import { ExportBar } from "./export-bar";
import { safeHttpUrl, type OdaCart, type OdaCompleteResult, type OdaPending } from "./oda";

type Failed = Extract<OdaCompleteResult, { kind: "failed" }>;

const failureKeys: Record<Failed["reason"], MessageKey> = {
  "state-invalid": "oda.problem.stateInvalid",
  "nothing-to-add": "oda.problem.nothingToAdd",
  "rate-limited": "problem.rateLimited",
  forbidden: "problem.forbidden",
  "not-configured": "oda.problem.notConfigured",
  unavailable: "oda.problem.unavailable",
  failed: "problem.generic",
};

const reasonKeys: Record<string, MessageKey> = {
  "no-match": "oda.reason.no-match",
  unavailable: "oda.reason.unavailable",
  rejected: "oda.reason.rejected",
};

const statusKeys: Record<string, MessageKey> = {
  done: "oda.result.done",
  partial: "oda.result.partial",
  "nothing-added": "oda.result.nothing-added",
  cancelled: "oda.result.cancelled",
};

/**
 * Everything here comes from Oda or from the API: product names, cart messages, reasons. It is all rendered as plain text
 * (React escapes it); the cart link is only followed when it is an http(s) address.
 */
export function OdaResult({ result, pending }: { result: OdaCompleteResult; pending: OdaPending }) {
  const { t } = useI18n();
  const back = (
    <Link className="button button-outline oda-back" to={`/shopping?week=${encodeURIComponent(pending.weekStart)}`}>
      {t("oda.result.back")}
    </Link>
  );
  return result.kind === "result" ? (
    <CartResult cart={result.cart} back={back} />
  ) : (
    <FailedResult failed={result} pending={pending} back={back} />
  );
}

/** A formatted quantity, the raw text when it is not a number, or null when there is nothing to show (never "NaN"). */
export function quantityText(value: unknown, format: (n: number) => string): string | null {
  if (typeof value === "number") return Number.isFinite(value) ? format(value) : null;
  if (typeof value !== "string") return null;
  const text = value.trim();
  if (text === "" || text.toLowerCase() === "nan") return null;
  const n = Number(text);
  return Number.isFinite(n) ? format(n) : text;
}

function AddedList({ lines }: { lines: OdaCart["added"] }) {
  const { t, formatNumber } = useI18n();
  return (
    <ul className="oda-list" data-testid="oda-added">
      {lines.map((l, i) => (
        <li key={`${l.itemId}-${i}`}>
          {(() => {
            const quantity = quantityText(l.quantity, formatNumber);
            return quantity === null
              ? t("oda.result.addedLineNoQuantity", { name: l.name, productName: l.productName })
              : t("oda.result.addedLine", { name: l.name, productName: l.productName, quantity });
          })()}
        </li>
      ))}
    </ul>
  );
}

function CartResult({ cart, back }: { cart: OdaCart; back: React.ReactNode }) {
  const { t } = useI18n();
  const cartUrl = safeHttpUrl(cart.cartUrl);
  const status = statusKeys[cart.status];
  return (
    <div className="oda-result" data-testid="oda-result" data-status={cart.status}>
      <p role="status" className="oda-status">
        <strong>{status ? t(status) : t("problem.generic")}</strong>
      </p>
      {cart.added.length > 0 && (
        <section aria-labelledby="oda-added-title">
          <h2 id="oda-added-title">{t("oda.result.added")}</h2>
          <AddedList lines={cart.added} />
        </section>
      )}
      {cart.notAdded.length > 0 && (
        <section aria-labelledby="oda-not-added-title">
          <h2 id="oda-not-added-title">{t("oda.result.notAdded")}</h2>
          <ul className="oda-list" data-testid="oda-not-added">
            {cart.notAdded.map((l, i) => (
              <li key={`${l.itemId}-${i}`}>
                {t("oda.result.notAddedLine", { name: l.name, reason: t(reasonKeys[l.reason] ?? "oda.reason.unknown") })}
              </li>
            ))}
          </ul>
        </section>
      )}
      {cart.cartMessages.length > 0 && (
        <section aria-labelledby="oda-messages-title">
          <h2 id="oda-messages-title">{t("oda.result.messages")}</h2>
          <ul className="oda-list" data-testid="oda-messages">
            {cart.cartMessages.map((m, i) => (
              <li key={i}>{m}</li>
            ))}
          </ul>
        </section>
      )}
      {cart.status !== "cancelled" && (
        <>
          <p className="shop-hint">{t("oda.result.cartHint")}</p>
          {cartUrl && (
            <a className="button oda-cart-link" href={cartUrl} target="_blank" rel="noopener noreferrer">
              {t("oda.result.openCart")}
            </a>
          )}
        </>
      )}
      {!cart.connectionClosed && (
        <p role="alert" className="form-error" data-testid="oda-not-closed">
          {t("oda.result.notClosed")}
        </p>
      )}
      {back}
    </div>
  );
}

function FailedResult({ failed, pending, back }: { failed: Failed; pending: OdaPending; back: React.ReactNode }) {
  const { t } = useI18n();
  return (
    <div className="oda-result" data-testid="oda-failed" data-reason={failed.reason}>
      <h2>{t("oda.result.failedTitle")}</h2>
      <p role="alert" className="form-error">
        {t(failureKeys[failed.reason])}
      </p>
      {failed.added.length > 0 && (
        <section aria-labelledby="oda-added-before">
          <p id="oda-added-before">{t("oda.result.addedBefore")}</p>
          <AddedList lines={failed.added} />
        </section>
      )}
      {failed.connectionClosed === false && (
        <p role="alert" className="form-error" data-testid="oda-not-closed">
          {t("oda.result.notClosed")}
        </p>
      )}
      {failed.offerText && (
        <section aria-labelledby="oda-text-title" data-testid="oda-text-fallback">
          <p id="oda-text-title">{t("oda.result.useText")}</p>
          <ExportBar householdId={pending.householdId} weekStart={pending.weekStart} online />
        </section>
      )}
      {back}
    </div>
  );
}
